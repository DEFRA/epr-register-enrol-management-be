using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-784 remediation: a 2027 renewal's accreditation number is the
/// operator's 2026 accreditation number with the year changed (RA-783), which
/// the operator backend now keeps on each application and sends with new
/// submissions as <c>payload.lastYearsAccreditationNumber</c>
/// (<c>{ year, accreditationNumber }</c>, RA-763). Work items submitted before
/// that hold none. This one-off gives every existing 2027 re-accreditation work
/// item its 2026 accreditation number from the 5 November 2026 public register
/// (<see cref="PublicRegister2026"/>). It never changes an issued number:
/// numbers issued wrongly are corrected separately (RA-785). The operator
/// backend's RA-784 migration does the same to its own copy; this one also
/// leaves evidence of each change in the work item's audit log (Application
/// history).
///
/// <para><strong>For each 2027 work item:</strong></para>
/// <list type="number">
///   <item>A work item holding J. and A. Young's registration number as the
///   register prints it, missing its last letter
///   (<see cref="PublicRegister2026.JAndAYoungPublishedRegistrationNumber"/>),
///   is reported: a 2027 number can't be issued until it ends PL. It is not
///   corrected here.</item>
///   <item>Find its register row by <c>payload.registrationNumber</c>.</item>
///   <item>Check the row is this work item's registration: its
///   <c>operatorOrgNumber</c> against the org digits in the registration number
///   or the register's Org ID column (an organisation can hold a registration
///   carrying another org's digits, such as Encirc's 500069 holding
///   R26ER5002260055GR), and its material code against <c>material</c>, each
///   when present. A mismatch is reported and the work item skipped.</item>
///   <item>Last year's number: added, with a
///   <see cref="AddedAuditAction"/> entry, where the work item holds none; left
///   alone where it holds the same; reported, never overwritten, where it holds
///   a different one. A row with no accreditation means the operator was
///   registered only in 2026: nothing to add.</item>
///   <item>An issued <c>accreditationId</c> is checked against the register
///   and reported if it doesn't fit (RA-783's rules): a renewal must keep last
///   year's agency, process type, org and sequence; a first-time number must
///   keep the registration's agency, process type and org. Only reported,
///   never corrected.</item>
/// </list>
///
/// <para>Never creates a work item and never changes state. Every work item is
/// planned before anything is written. Each write is one atomic update of the
/// field and its audit entry, filtered on the field still holding what was
/// planned from (<see cref="IWorkItemPersistence.CorrectPayloadFieldAsync"/>),
/// so a re-run is a no-op and an audit entry never describes a change that did
/// not happen.</para>
///
/// <para><strong>Gates</strong> (keys mirroring the operator backend's RA-784
/// migration), all required before a single write:</para>
/// <list type="number">
///   <item><see cref="EnabledConfigKey"/>: off by default. On its own it is a
///   dry run that logs each work item's outcome and a summary, and writes
///   nothing.</item>
///   <item><see cref="ApplyConfigKey"/>: writes the numbers and their audit
///   entries.</item>
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
    public const string PayloadKey = "lastYearsAccreditationNumber";

    // Contract with management-fe (renders the details rows). Do not change one side.
    public const string AddedAuditAction = "last-years-accreditation-number-added";
    public const string AddedAuditActionDisplayName = "Last year's accreditation number added";
    public const string AuditReason = "RA-784";
    public const string AuditSource = "Public register of reprocessors and exporters, 5 November 2026";
    public const string AuditCreatedBy = "migration";
    public const string AuditCreatedByName = "Migration: RA-784 last year's accreditation number";

    internal const string RegistrationNumberField = "registrationNumber";
    internal const string OperatorOrgNumberField = "operatorOrgNumber";
    internal const string MaterialField = "material";
    internal const string GlassRecyclingProcessField = "glassRecyclingProcess";
    internal const string AccreditationYearField = "accreditationYear";

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
        RegistrationNumberIncomplete,
        NotOnTheRegister,
    }

    internal enum IssuedNumber
    {
        NotIssued,
        FitsTheRegister,
        DoesNotFitTheRegister,
        NotChecked,
    }

    private sealed record Plan(
        WorkItem Item,
        Outcome Outcome,
        string? RegisterAccreditationNumber,
        IssuedNumber Issued,
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
                "RA-784 last year's accreditation backfill failed; continuing startup. Will retry on next boot."
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
        logger.LogInformation("RA-784 last year's accreditation backfill starting. Mode={Mode}", mode);

        var items = await persistence.FindByPayloadFieldAsync(
            ReAccreditationType.Id,
            AccreditationYearField,
            AccreditationYear,
            cancellationToken
        );

        // Plan every work item before writing any.
        var plans = items.OrderBy(i => i.SubmittedAt).Select(PlanFor).ToList();

        foreach (var plan in plans)
        {
            await ReportAndApplyAsync(persistence, plan, mode, confirmedBy, cancellationToken);
        }

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
            "RA-784 last year's accreditation backfill: {ApplyKey} is set but {SpotCheckKey} is not, "
                + "so running as a DRY RUN. Set it to the name of whoever reviewed the dry-run "
                + "output against the live records.",
            ApplyConfigKey,
            SpotCheckConfirmedByConfigKey
        );
        return null;
    }

    // ------------------------------- planning -------------------------------

    private static Plan PlanFor(WorkItem item)
    {
        var payload = item.Payload ?? new BsonDocument();
        var registrationNumber = ReadString(payload, RegistrationNumberField);

        if (registrationNumber is null)
        {
            return NotPlanned(item, Outcome.NotOnTheRegister, "it holds no registration number");
        }

        if (registrationNumber == PublicRegister2026.JAndAYoungPublishedRegistrationNumber)
        {
            return NotPlanned(
                item,
                Outcome.RegistrationNumberIncomplete,
                $"registration number {registrationNumber} is missing its last letter, as the register "
                    + "prints it; it must end PL before a 2027 number can be issued"
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

        if (IdentityMismatch(payload, registrationNumber, row) is { } mismatch)
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

        var (issued, issuedDetail) = CheckIssuedNumber(
            ReAccreditationAccreditationNumber.Read(item.Payload),
            registrationNumber,
            row.AccreditationNumber
        );
        return new Plan(
            item,
            outcome,
            row.AccreditationNumber,
            issued,
            issuedDetail is null ? detail : $"{detail}; {issuedDetail}"
        );
    }

    private static Plan NotPlanned(WorkItem item, Outcome outcome, string detail)
    {
        var issued = ReAccreditationAccreditationNumber.Read(item.Payload);
        return issued is null
            ? new Plan(item, outcome, null, IssuedNumber.NotIssued, detail)
            : new Plan(
                item,
                outcome,
                null,
                IssuedNumber.NotChecked,
                $"{detail}; issued {issued} could not be checked"
            );
    }

    /// <summary>
    /// Why the register row is not this work item's registration, or null.
    /// The registration number carries its own identity: R + YY + agency +
    /// R|X + org (6) + sequence (4) + material. The org is accepted from either
    /// the registration number or the register's Org ID column: an organisation
    /// can hold a registration carrying another org's digits. Older submissions
    /// predate operatorOrgNumber, so each field is checked only when present.
    /// </summary>
    internal static string? IdentityMismatch(
        BsonDocument payload,
        string registrationNumber,
        PublicRegister2026Entry row
    )
    {
        var problems = new List<string>();
        var orgDigits = registrationNumber[5..11];
        if (
            payload.TryGetValue(OperatorOrgNumberField, out var orgNumber)
            && !orgNumber.IsBsonNull
            && !(
                BsonWholeNumber.TryGet(orgNumber, 0, 999_999, out var number)
                && (
                    number.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) == orgDigits
                    || number == row.OrgId
                )
            )
        )
        {
            problems.Add(
                $"its operatorOrgNumber is {orgNumber} but the registration number's org is {orgDigits} "
                    + $"and the register's is {row.OrgId}"
            );
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

    // RA-783's rules, for an issued number: a renewal keeps everything of last
    // year's number but the year and material code; a first-time number keeps
    // the registration's agency, process type and org, with a random sequence.
    // The material code is the application's, so it isn't compared (a 2026
    // glass number ends GL).
    private static (IssuedNumber, string?) CheckIssuedNumber(
        string? issued,
        string registrationNumber,
        string? lastYearsAccreditationNumber
    )
    {
        if (issued is null)
        {
            return (IssuedNumber.NotIssued, null);
        }

        var year = $"{AccreditationYear % 100:D2}";
        var (source, kept, rule) = lastYearsAccreditationNumber is { } lastYears
            ? (lastYears, lastYears[3..^2], "a renewal of")
            : (registrationNumber, registrationNumber[3..11], "a first-time number from");

        var fits =
            issued.Length == 17
            && issued[0] == 'A'
            && issued[1..3] == year
            && issued[3..].StartsWith(kept, StringComparison.Ordinal);
        return fits
            ? (IssuedNumber.FitsTheRegister, $"issued {issued} fits the register")
            : (
                IssuedNumber.DoesNotFitTheRegister,
                $"issued {issued} is not {rule} {source}; reported only (RA-785 corrects numbers)"
            );
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
            "RA-784 {Mode}: work item {WorkItemId} ({ApplicationReference}, org {OrgNumber} "
                + "{OrganisationName}, registration {RegistrationNumber}): {Outcome}, issued number "
                + "{IssuedNumber}. {Detail}",
            mode,
            plan.Item.Id,
            ReadString(plan.Item.Payload, "applicationReference"),
            ReadOrgNumber(plan.Item.Payload),
            ReadString(plan.Item.Payload, OrganisationNameField),
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
    }

    private void LogWrite(bool written, Plan plan, string change, string confirmedBy)
    {
        if (!written)
        {
            // Filtered on the planned value, so this only happens if something
            // changed it between planning and now. Nothing was written; the
            // next run re-plans from fresh data.
            logger.LogWarning(
                "RA-784 work item {WorkItemId} changed after planning, so nothing was written to it "
                    + "({Change}). Re-run the dry run to see its current state.",
                plan.Item.Id,
                change
            );
            return;
        }

        logger.LogInformation(
            "RA-784 APPLIED: work item {WorkItemId} ({ApplicationReference}, org {OrgNumber} "
                + "{OrganisationName}) {Change}. Spot check confirmed by {ConfirmedBy}",
            plan.Item.Id,
            ReadString(plan.Item.Payload, "applicationReference"),
            ReadOrgNumber(plan.Item.Payload),
            ReadString(plan.Item.Payload, OrganisationNameField),
            change,
            confirmedBy
        );
    }

    private static bool NeedsAPerson(Plan plan) =>
        plan.Outcome
            is Outcome.HoldsADifferentNumber
                or Outcome.IdentityMismatch
                or Outcome.RegistrationNumberIncomplete
                or Outcome.NotOnTheRegister
        || plan.Issued is IssuedNumber.DoesNotFitTheRegister or IssuedNumber.NotChecked;

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
            "RA-784 last year's accreditation backfill complete. Mode={Mode}, work items={Count}. "
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

    // The organisation's number and name are a business's, as on the public register, so they may be logged; they
    // let whoever reviews the dry run see which organisation each line is about.
    private const string OrganisationNameField = "organisationName";

    private static string? ReadOrgNumber(BsonDocument? payload) =>
        payload is not null
        && payload.TryGetValue(OperatorOrgNumberField, out var value)
        && !value.IsBsonNull
            ? value.ToString()
            : null;

    private static string? ReadString(BsonDocument? payload, string field) =>
        payload is not null && payload.TryGetValue(field, out var value) && value.IsString
            ? value.AsString
            : null;
}
