using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-763 remediation: 2027 accreditation numbers are derived from the
/// operator's 2026 accreditation number, which the operator backend now keeps
/// on each application and sends with new submissions as
/// <c>payload.lastYearsAccreditation</c> (<c>{ year, accreditationNumber }</c>).
/// Work items submitted before that hold none. This one-off gives every
/// existing 2027 re-accreditation work item its 2026 accreditation number from
/// the 5 November 2026 public register (<see cref="PublicRegister2026"/>), and
/// corrects the one 2027 number already issued from the wrong source
/// (<see cref="Corrections"/>). The operator backend's RA-763 migration does
/// the same to its own copy; this one also leaves evidence of each change in
/// the work item's audit log (Application history).
///
/// <para><strong>For each 2027 work item:</strong></para>
/// <list type="number">
///   <item>Find its register row by <c>payload.registrationNumber</c>. A row in
///   <see cref="PublicRegister2026.HeldBack"/> is a register error awaiting
///   confirmation: reported, not used.</item>
///   <item>Check the row is this work item's registration: the org digits in
///   the registration number against <c>operatorOrgNumber</c>, and its
///   material code against <c>material</c>, each when present. A mismatch is
///   reported and the work item skipped.</item>
///   <item>Last year's number: added, with a
///   <see cref="AddedAuditAction"/> entry, where the work item holds none; left
///   alone where it holds the same; reported, never overwritten, where it holds
///   a different one. A row with no accreditation means the operator was
///   registered only in 2026: nothing to add.</item>
///   <item>An issued <c>accreditationId</c> is compared with the one derived
///   from the register. A mismatch named in <see cref="Corrections"/> is
///   corrected with an <see cref="CorrectedAuditAction"/> entry (the RA-691
///   action, which management-fe already renders); any other is reported
///   only.</item>
/// </list>
///
/// <para>Never creates a work item and never changes state. Every work item is
/// planned before anything is written. Each write is one atomic update of the
/// field and its audit entry, filtered on the field still holding what was
/// planned from (<see cref="IWorkItemPersistence.CorrectPayloadFieldAsync"/>),
/// so a re-run is a no-op and an audit entry never describes a change that did
/// not happen.</para>
///
/// <para><strong>Gates</strong> (keys mirroring the operator backend's RA-763
/// migration), all required before a single write:</para>
/// <list type="number">
///   <item><see cref="EnabledConfigKey"/>: off by default. On its own it is a
///   dry run that logs each work item's outcome and a summary, and writes
///   nothing.</item>
///   <item><see cref="ApplyConfigKey"/>: writes the numbers, the correction and
///   their audit entries.</item>
///   <item><see cref="SpotCheckConfirmedByConfigKey"/>: names whoever reviewed
///   the dry-run output; without it an Apply run logs an error and stays a dry
///   run.</item>
/// </list>
///
/// <para>A hosted lifecycle service like
/// <see cref="ReAccreditationOrganisationNameCorrectionMigration"/>, so it runs
/// in <see cref="StartedAsync"/>, after the journey-test fixtures are
/// reset.</para>
/// </summary>
internal sealed class ReAccreditationLastYearsAccreditationBackfillMigration(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<ReAccreditationLastYearsAccreditationBackfillMigration> logger,
    TimeProvider? timeProvider = null
) : IHostedLifecycleService
{
    public const string ConfigSection = "Migrations:Ra763LastYearsAccreditationBackfill";
    public const string EnabledConfigKey = ConfigSection + ":Enabled";
    public const string ApplyConfigKey = ConfigSection + ":Apply";
    public const string SpotCheckConfirmedByConfigKey = ConfigSection + ":SpotCheckConfirmedBy";

    public const int AccreditationYear = 2027;
    public const int LastYear = AccreditationYear - 1;

    /// <summary>Payload key the operator backend sends for new submissions.</summary>
    public const string PayloadKey = "lastYearsAccreditation";

    // Contract with management-fe (renders the details rows). Do not change one side.
    public const string AddedAuditAction = "last-years-accreditation-number-added";
    public const string AddedAuditActionDisplayName = "Last year's accreditation number added";
    public const string CorrectedAuditAction =
        ReAccreditationAccreditationNumberCorrectionMigration.AuditAction;
    public const string CorrectedAuditActionDisplayName =
        ReAccreditationAccreditationNumberCorrectionMigration.AuditActionDisplayName;
    public const string AuditReason = "RA-763";
    public const string AuditSource = "Public register of reprocessors and exporters, 5 November 2026";
    public const string AuditCreatedBy = "migration";
    public const string AuditCreatedByName = "Migration: RA-763 last year's accreditation number";

    internal const string RegistrationNumberField = "registrationNumber";
    internal const string OperatorOrgNumberField = "operatorOrgNumber";
    internal const string MaterialField = "material";
    internal const string GlassRecyclingProcessField = "glassRecyclingProcess";
    internal const string AccreditationYearField = "accreditationYear";

    /// <summary>An issued 2027 number to correct, confirmed against the register by a person.</summary>
    internal sealed record Correction(
        string OrganisationName,
        string RegistrationNumber,
        string Incorrect,
        string Corrected
    );

    /// <summary>The only issued numbers this corrects; any other mismatch is reported only.</summary>
    internal static readonly IReadOnlyList<Correction> Corrections =
    [
        new(
            "CLEARFIELD ENVIROTECH LIMITED",
            "R26ER5003240079PL",
            "A27ER5003240079PL",
            "A27ER5003240164PL"
        ),
    ];

    /// <summary>What one run did. Returned for tests; the logs say the same.</summary>
    internal enum RunResult
    {
        Disabled,
        DryRun,
        Applied,
    }

    internal enum Outcome
    {
        Added,
        AlreadyHeld,
        RegisteredOnly,
        HoldsADifferentNumber,
        IdentityMismatch,
        HeldBack,
        NotOnTheRegister,
    }

    internal enum IssuedNumber
    {
        NotIssued,
        Correct,
        Corrected,
        AlreadyCorrected,
        WrongReportedOnly,
        CorrectionBlocked,
    }

    private sealed record Plan(
        WorkItem Item,
        Outcome Outcome,
        string? RegisterAccreditationNumber,
        IssuedNumber Issued,
        string? Expected,
        string Detail
    );

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    private bool IsEnabled => configuration.GetValue(EnabledConfigKey, false);

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return;
        }

        try
        {
            // Resolved inside the try: constructing the Mongo-backed
            // persistence probes the database.
            var persistence = serviceProvider.GetRequiredService<IWorkItemPersistence>();
            await ApplyAsync(persistence, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "RA-763 last year's accreditation backfill failed; continuing startup. Will retry on next boot."
            );
        }
    }

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The audit entry for a number added from the register.</summary>
    internal static WorkItemAuditEntry BuildAddedAuditEntry(string accreditationNumber, DateTime createdAt) =>
        new()
        {
            Action = AddedAuditAction,
            ActionDisplayName = AddedAuditActionDisplayName,
            CreatedAt = createdAt,
            CreatedBy = AuditCreatedBy,
            CreatedByName = AuditCreatedByName,
            Details = new Dictionary<string, string?>
            {
                ["year"] = LastYear.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["accreditationNumber"] = accreditationNumber,
                ["source"] = AuditSource,
                ["reason"] = AuditReason,
            },
        };

    /// <summary>The audit entry for a corrected issued number: the RA-691 shape, with this reason.</summary>
    internal static WorkItemAuditEntry BuildCorrectedAuditEntry(string from, string to, DateTime createdAt) =>
        new()
        {
            Action = CorrectedAuditAction,
            ActionDisplayName = CorrectedAuditActionDisplayName,
            CreatedAt = createdAt,
            CreatedBy = AuditCreatedBy,
            CreatedByName = AuditCreatedByName,
            Details = new Dictionary<string, string?>
            {
                ["from"] = from,
                ["to"] = to,
                ["reason"] = AuditReason,
            },
        };

    internal async Task<RunResult> ApplyAsync(
        IWorkItemPersistence persistence,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(persistence);

        if (!IsEnabled)
        {
            return RunResult.Disabled;
        }

        var confirmedBy = AuthorisedReviewer();
        var mode = confirmedBy is null ? "DRY RUN (nothing will be written)" : "APPLY";
        logger.LogInformation("RA-763 last year's accreditation backfill starting. Mode={Mode}", mode);

        var items = await persistence.FindByPayloadFieldAsync(
            ReAccreditationType.Id,
            AccreditationYearField,
            AccreditationYear,
            cancellationToken
        );

        // Plan every work item before writing any.
        var plans = new List<Plan>();
        foreach (var item in items.OrderBy(i => i.SubmittedAt))
        {
            plans.Add(await PlanAsync(persistence, item, cancellationToken));
        }

        foreach (var plan in plans)
        {
            await ReportAndApplyAsync(persistence, plan, mode, confirmedBy, cancellationToken);
        }

        ReportUnusedCorrections(plans);
        ReportSummary(plans, mode);
        return confirmedBy is null ? RunResult.DryRun : RunResult.Applied;
    }

    /// <summary>Who authorised an applied run, or null for a dry run.</summary>
    private string? AuthorisedReviewer()
    {
        if (!configuration.GetValue(ApplyConfigKey, false))
        {
            return null;
        }

        var confirmedBy = configuration.GetValue<string?>(SpotCheckConfirmedByConfigKey);
        if (!string.IsNullOrWhiteSpace(confirmedBy))
        {
            return confirmedBy;
        }

        logger.LogError(
            "RA-763 last year's accreditation backfill: {ApplyKey} is set but {SpotCheckKey} is not, "
                + "so running as a DRY RUN. Set it to the name of whoever reviewed the dry-run "
                + "output against the live records.",
            ApplyConfigKey,
            SpotCheckConfirmedByConfigKey
        );
        return null;
    }

    // ------------------------------- planning -------------------------------

    private static async Task<Plan> PlanAsync(
        IWorkItemPersistence persistence,
        WorkItem item,
        CancellationToken cancellationToken
    )
    {
        var payload = item.Payload ?? new BsonDocument();
        var registrationNumber = ReadString(payload, RegistrationNumberField);

        if (registrationNumber is null)
        {
            return NotPlanned(item, Outcome.NotOnTheRegister, "it holds no registration number");
        }

        if (PublicRegister2026.HeldBack.ContainsKey(registrationNumber))
        {
            return NotPlanned(
                item,
                Outcome.HeldBack,
                $"register row {registrationNumber} is held back as a register error until it is confirmed"
            );
        }

        if (!PublicRegister2026.ByRegistrationNumber.TryGetValue(registrationNumber, out var row))
        {
            return NotPlanned(
                item,
                Outcome.NotOnTheRegister,
                $"registration {registrationNumber} is not on the register"
            );
        }

        if (IdentityMismatch(payload, registrationNumber) is { } mismatch)
        {
            return NotPlanned(item, Outcome.IdentityMismatch, mismatch);
        }

        var held = ReadLastYears(payload);
        var (outcome, detail) = row.AccreditationNumber switch
        {
            null => (
                Outcome.RegisteredOnly,
                $"register row {registrationNumber} has no accreditation, so the operator was registered only in {LastYear}"
            ),
            var number when held is null => (
                Outcome.Added,
                $"register row {registrationNumber}: last year's accreditation {number}"
            ),
            var number when held.Value.Year == LastYear && held.Value.Number == number => (
                Outcome.AlreadyHeld,
                $"already holds last year's accreditation {number}"
            ),
            var number => (
                Outcome.HoldsADifferentNumber,
                $"holds {held.Value.Year} accreditation {held.Value.Number}, but register row "
                    + $"{registrationNumber} says {LastYear} accreditation {number}; left as it is"
            ),
        };

        var (issued, expected, issuedDetail) = await PlanIssuedNumberAsync(
            persistence,
            item,
            registrationNumber,
            row.AccreditationNumber,
            cancellationToken
        );
        return new Plan(
            item,
            outcome,
            row.AccreditationNumber,
            issued,
            expected,
            issuedDetail is null ? detail : $"{detail}; {issuedDetail}"
        );
    }

    private static Plan NotPlanned(WorkItem item, Outcome outcome, string detail)
    {
        var issued = ReAccreditationAccreditationNumber.Read(item.Payload);
        return issued is null
            ? new Plan(item, outcome, null, IssuedNumber.NotIssued, null, detail)
            : new Plan(
                item,
                outcome,
                null,
                IssuedNumber.WrongReportedOnly,
                null,
                $"{detail}; issued {issued} could not be checked"
            );
    }

    /// <summary>
    /// Why the register row is not this work item's registration, or null.
    /// The registration number carries its own identity: R + YY + agency +
    /// R|X + org (6) + sequence (4) + material. The register's Org ID column is
    /// not used: it is wrong on some rows. Older submissions predate
    /// operatorOrgNumber, so each field is checked only when present.
    /// </summary>
    internal static string? IdentityMismatch(BsonDocument payload, string registrationNumber)
    {
        var problems = new List<string>();
        var orgDigits = registrationNumber[5..11];
        if (
            payload.TryGetValue(OperatorOrgNumberField, out var orgNumber)
            && !orgNumber.IsBsonNull
            && !(
                BsonWholeNumber.TryGet(orgNumber, 0, 999_999, out var number)
                && number.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) == orgDigits
            )
        )
        {
            problems.Add($"its operatorOrgNumber is {orgNumber} but the registration number's org is {orgDigits}");
        }

        var material = ReadString(payload, MaterialField);
        var materialCode = registrationNumber[^2..];
        if (material is not null && !MaterialCodesFor(material, ReadString(payload, GlassRecyclingProcessField)).Contains(materialCode))
        {
            problems.Add($"its material is {material} but the registration number's is {materialCode}");
        }

        return problems.Count == 0
            ? null
            : $"register row {registrationNumber} is not this work item's registration: "
                + string.Join("; ", problems);
    }

    // The operator backend's material codes (RegulatoryReferenceCodes). Glass
    // splits by recycling process; a glass item without one can be either.
    private static string[] MaterialCodesFor(string material, string? glassRecyclingProcess) =>
        material.ToLowerInvariant() switch
        {
            "steel" => ["ST"],
            "wood" => ["WO"],
            "aluminium" => ["AL"],
            "fibre" => ["FB"],
            "paper" => ["PA"],
            "plastic" => ["PL"],
            "glass" => glassRecyclingProcess switch
            {
                "glass_re_melt" => ["GR"],
                "glass_other" => ["GO"],
                _ => ["GR", "GO"],
            },
            _ => [],
        };

    private static async Task<(IssuedNumber, string?, string?)> PlanIssuedNumberAsync(
        IWorkItemPersistence persistence,
        WorkItem item,
        string registrationNumber,
        string? lastYearsAccreditation,
        CancellationToken cancellationToken
    )
    {
        var issued = ReAccreditationAccreditationNumber.Read(item.Payload);
        if (issued is null)
        {
            return (IssuedNumber.NotIssued, null, null);
        }

        // The operator backend's derivation (AccreditationReferenceDerivation):
        // A + the year applied for + everything after the source's year.
        var expected = $"A{AccreditationYear % 100:D2}{(lastYearsAccreditation ?? registrationNumber)[3..]}";
        var correction = Corrections.FirstOrDefault(c => c.RegistrationNumber == registrationNumber);

        if (issued == expected)
        {
            return correction is not null && issued == correction.Corrected
                ? (IssuedNumber.AlreadyCorrected, expected, $"issued {issued} is already corrected")
                : (IssuedNumber.Correct, expected, $"issued {issued} matches the register");
        }

        if (correction is null || correction.Incorrect != issued || correction.Corrected != expected)
        {
            return (
                IssuedNumber.WrongReportedOnly,
                expected,
                $"issued {issued}, but the register gives {expected}; reported only"
            );
        }

        var holders = await persistence.FindByPayloadFieldAsync(
            ReAccreditationType.Id,
            ReAccreditationAccreditationNumber.PayloadKey,
            correction.Corrected,
            cancellationToken
        );
        if (holders.Count > 0)
        {
            return (
                IssuedNumber.CorrectionBlocked,
                expected,
                $"issued {issued} should be {correction.Corrected}, but work item {holders[0].Id} "
                    + "already holds that; not corrected"
            );
        }

        return (IssuedNumber.Corrected, expected, $"issued {issued} is corrected to {correction.Corrected}");
    }

    // ------------------------------- reporting and writing -------------------------------

    private async Task ReportAndApplyAsync(
        IWorkItemPersistence persistence,
        Plan plan,
        string mode,
        string? confirmedBy,
        CancellationToken cancellationToken
    )
    {
        var level = NeedsAPerson(plan) ? LogLevel.Warning : LogLevel.Information;
        logger.Log(
            level,
            "RA-763 {Mode}: work item {WorkItemId} ({ApplicationReference}, registration "
                + "{RegistrationNumber}): {Outcome}, issued number {IssuedNumber}. {Detail}",
            mode,
            plan.Item.Id,
            ReadString(plan.Item.Payload, "applicationReference"),
            ReadString(plan.Item.Payload, RegistrationNumberField),
            plan.Outcome,
            plan.Issued,
            plan.Detail
        );

        if (confirmedBy is null)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (plan.Outcome == Outcome.Added)
        {
            var number = plan.RegisterAccreditationNumber!;
            var written = await persistence.CorrectPayloadFieldAsync(
                plan.Item.Id,
                PayloadKey,
                BsonNull.Value,
                new BsonDocument { ["year"] = LastYear, ["accreditationNumber"] = number },
                BuildAddedAuditEntry(number, now),
                cancellationToken
            );
            LogWrite(written, plan, $"now holds {LastYear} accreditation {number}", confirmedBy);
        }

        if (plan.Issued == IssuedNumber.Corrected)
        {
            var incorrect = ReAccreditationAccreditationNumber.Read(plan.Item.Payload)!;
            var written = await persistence.CorrectPayloadFieldAsync(
                plan.Item.Id,
                ReAccreditationAccreditationNumber.PayloadKey,
                incorrect,
                plan.Expected!,
                BuildCorrectedAuditEntry(incorrect, plan.Expected!, now),
                cancellationToken
            );
            LogWrite(written, plan, $"accreditation number {incorrect} → {plan.Expected}", confirmedBy);
        }
    }

    private void LogWrite(bool written, Plan plan, string change, string confirmedBy)
    {
        if (!written)
        {
            // Filtered on the planned value, so this only happens if something
            // changed it between planning and now. Nothing was written; the
            // next run re-plans from fresh data.
            logger.LogWarning(
                "RA-763 work item {WorkItemId} changed after planning, so nothing was written to it "
                    + "({Change}). Re-run the dry run to see its current state.",
                plan.Item.Id,
                change
            );
            return;
        }

        logger.LogInformation(
            "RA-763 APPLIED: work item {WorkItemId} ({ApplicationReference}) {Change}. "
                + "Spot check confirmed by {ConfirmedBy}",
            plan.Item.Id,
            ReadString(plan.Item.Payload, "applicationReference"),
            change,
            confirmedBy
        );
    }

    private static bool NeedsAPerson(Plan plan) =>
        plan.Outcome
            is Outcome.HoldsADifferentNumber
                or Outcome.IdentityMismatch
                or Outcome.HeldBack
                or Outcome.NotOnTheRegister
        || plan.Issued is IssuedNumber.WrongReportedOnly or IssuedNumber.CorrectionBlocked;

    private void ReportUnusedCorrections(IReadOnlyList<Plan> plans)
    {
        foreach (var correction in Corrections)
        {
            var used = plans.Any(p =>
                p.Issued is IssuedNumber.Corrected or IssuedNumber.AlreadyCorrected or IssuedNumber.CorrectionBlocked
                && ReAccreditationAccreditationNumber.Read(p.Item.Payload) is { } issued
                && (issued == correction.Incorrect || issued == correction.Corrected)
            );
            if (!used)
            {
                logger.LogWarning(
                    "RA-763 {OrganisationName}: no 2027 work item on registration {RegistrationNumber} "
                        + "holds {Incorrect} or {Corrected}; nothing to correct",
                    correction.OrganisationName,
                    correction.RegistrationNumber,
                    correction.Incorrect,
                    correction.Corrected
                );
            }
        }
    }

    private void ReportSummary(IReadOnlyList<Plan> plans, string mode)
    {
        var outcomes = string.Join(
            ", ",
            Enum.GetValues<Outcome>().Select(o => $"{o}={plans.Count(p => p.Outcome == o)}")
        );
        var issued = string.Join(
            ", ",
            Enum.GetValues<IssuedNumber>().Select(i => $"{i}={plans.Count(p => p.Issued == i)}")
        );
        logger.LogInformation(
            "RA-763 last year's accreditation backfill complete. Mode={Mode}, work items={Count}. "
                + "Last year's accreditation: {Outcomes}. Issued numbers: {Issued}",
            mode,
            plans.Count,
            outcomes,
            issued
        );
    }

    private static (int Year, string Number)? ReadLastYears(BsonDocument payload)
    {
        if (
            !payload.TryGetValue(PayloadKey, out var value)
            || !value.IsBsonDocument
            || !value.AsBsonDocument.TryGetValue("year", out var year)
            || !BsonWholeNumber.TryGet(year, 2000, 2999, out var y)
            || ReadString(value.AsBsonDocument, "accreditationNumber") is not { } number
        )
        {
            return null;
        }

        return (y, number);
    }

    private static string? ReadString(BsonDocument? payload, string field) =>
        payload is not null && payload.TryGetValue(field, out var value) && value.IsString
            ? value.AsString
            : null;
}
