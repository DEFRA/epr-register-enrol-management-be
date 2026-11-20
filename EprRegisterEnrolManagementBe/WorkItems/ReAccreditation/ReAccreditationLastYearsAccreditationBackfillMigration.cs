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
/// <para><strong>Organisation by organisation (<c>operatorOrgNumber</c>), and
/// within one by registration number, for each 2027 work item:</strong></para>
/// <list type="number">
///   <item>A work item holding J. and A. Young's registration number as the
///   register prints it, missing its last letter
///   (<see cref="PublicRegister2026.JAndAYoungPublishedRegistrationNumber"/>),
///   is reported: a 2027 number can't be issued until it ends PL. It is not
///   corrected here.</item>
///   <item>Find its register row by its 2026 registration number,
///   <c>payload.registrationNumber</c>. The registration number is the only
///   key: the org digits in it don't always match the org that holds it
///   (Encirc, 500069, holds R26ER5002260055GR), so nothing else is compared. A
///   work item whose registration number isn't on the register is reported
///   with its org.</item>
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
/// <para>The run ends with a report: every work item whose registration
/// number isn't on the register (its org and registration number), and every
/// register row no 2027 work item used.</para>
///
/// <para>Never creates a work item and never changes state. Every work item is
/// planned before anything is written. Each write is one atomic update of the
/// field and its audit entry, filtered on the field still being empty
/// (<see cref="IWorkItemPersistence.CorrectPayloadFieldAsync"/>),
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
    internal const string ApplicationReferenceField = "applicationReference";

    // The organisation's number and name are a business's, as on the public register, so they may be logged; they
    // let whoever reviews the dry run see which organisation each line is about.
    internal const string OrganisationNameField = "organisationName";

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

    /// <summary>What happened to an Added plan's write. A dry run attempts none.</summary>
    internal enum Write
    {
        Written,
        LostTheRace,
        Failed,
    }

    private sealed record Plan(
        WorkItem Item,
        Outcome Outcome,
        string? RegisterRegistrationNumber,
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
            logger.LogError(
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
            Core.AccreditationYear.Field,
            AccreditationYear,
            cancellationToken
        );

        // Plan every work item before writing any.
        // Organisation by organisation; an item without its org number sorts after the rest.
        var plans = items
            .OrderBy(i => OrgNumberOf(i.Payload) ?? int.MaxValue)
            .ThenBy(i => ReadString(i.Payload, RegistrationNumberField), StringComparer.Ordinal)
            .ThenBy(i => i.SubmittedAt)
            .Select(PlanFor)
            .ToList();

        var writes = new List<Write>();
        foreach (var plan in plans)
        {
            if (await ReportAndApplyAsync(persistence, plan, mode, confirmedBy, cancellationToken) is { } write)
            {
                writes.Add(write);
            }
        }

        ReportNotOnTheRegister(plans, mode);
        ReportUnusedRegisterRows(plans, mode);
        ReportSummary(plans, writes, mode);
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

        var held = ReadLastYears(payload);
        if (held is null && HoldsAValue(payload, PayloadKey))
        {
            return NotPlanned(
                item,
                Outcome.HoldsADifferentNumber,
                $"its {PayloadKey} isn't a year and an accreditation number; left as it is"
            );
        }

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
            registrationNumber,
            row.AccreditationNumber,
            issued,
            issuedDetail is null ? detail : $"{detail}; {issuedDetail}"
        );
    }

    private static Plan NotPlanned(WorkItem item, Outcome outcome, string detail)
    {
        var issued = ReAccreditationAccreditationNumber.Read(item.Payload);
        return issued is null
            ? new Plan(item, outcome, null, null, IssuedNumber.NotIssued, detail)
            : new Plan(
                item,
                outcome,
                null,
                null,
                IssuedNumber.NotChecked,
                $"{detail}; issued {issued} could not be checked"
            );
    }

    // RA-783's rules, for an issued number: a renewal keeps everything of last
    // year's number but the year and material code; a first-time number keeps
    // the registration's agency, process type and org, with a random sequence.
    // The material code is the application's, so it isn't compared (a 2026
    // glass number can end GL, as Comhairle Nan Eilean Siar's does).
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

    // Null when no write was attempted: a dry run, or a plan that isn't Added.
    private async Task<Write?> ReportAndApplyAsync(
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
            ReadString(plan.Item.Payload, ApplicationReferenceField),
            ReadOrgNumber(plan.Item.Payload),
            ReadString(plan.Item.Payload, OrganisationNameField),
            ReadString(plan.Item.Payload, RegistrationNumberField),
            plan.Outcome,
            plan.Issued,
            plan.Detail
        );

        if (confirmedBy is null || plan.Outcome != Outcome.Added)
        {
            return null;
        }

        // Only an Added plan reaches here, and PlanFor only gives that outcome to a register row with a number.
        var number = plan.RegisterAccreditationNumber!;
        bool written;
        try
        {
            written = await persistence.CorrectPayloadFieldAsync(
                plan.Item.Id,
                PayloadKey,
                BsonNull.Value,
                new BsonDocument { ["year"] = LastYear, ["accreditationNumber"] = number },
                BuildAddedAuditEntry(number, _timeProvider.GetUtcNow().UtcDateTime),
                cancellationToken
            );
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // One work item's failure doesn't stop the run: the rest are still written and the report
            // still logged. The write is atomic, so nothing was written to this one; the next run retries it.
            logger.LogError(
                ex,
                "RA-784 work item {WorkItemId}: the write failed, so nothing was written to it. The next run "
                    + "retries it.",
                plan.Item.Id
            );
            return Write.Failed;
        }

        if (!written)
        {
            // Filtered on the field still being empty, so this only happens if something gave it a
            // number between planning and now. Nothing was written; the next run re-plans from fresh data.
            logger.LogWarning(
                "RA-784 work item {WorkItemId} gained last year's accreditation after planning, so nothing "
                    + "was written to it. Re-run the dry run to see its current state.",
                plan.Item.Id
            );
            return Write.LostTheRace;
        }

        logger.LogInformation(
            "RA-784 APPLIED: work item {WorkItemId} ({ApplicationReference}, org {OrgNumber} "
                + "{OrganisationName}) now holds {Year} accreditation {AccreditationNumber}. Spot check "
                + "confirmed by {ConfirmedBy}",
            plan.Item.Id,
            ReadString(plan.Item.Payload, ApplicationReferenceField),
            ReadOrgNumber(plan.Item.Payload),
            ReadString(plan.Item.Payload, OrganisationNameField),
            LastYear,
            number,
            confirmedBy
        );
        return Write.Written;
    }

    private static bool NeedsAPerson(Plan plan) =>
        plan.Outcome
            is Outcome.HoldsADifferentNumber
                or Outcome.RegistrationNumberIncomplete
                or Outcome.NotOnTheRegister
        || plan.Issued is IssuedNumber.DoesNotFitTheRegister or IssuedNumber.NotChecked;

    // The end-of-run report, part 1: every work item whose registration number
    // isn't on the register, with the org and registration number it holds.
    private void ReportNotOnTheRegister(IReadOnlyList<Plan> plans, string mode)
    {
        var missing = plans
            .Where(p => p.Outcome is Outcome.NotOnTheRegister or Outcome.RegistrationNumberIncomplete)
            .ToList();
        logger.LogWarning(
            "RA-784 {Mode} report: {Count} 2027 work items whose registration number is not on the register",
            mode,
            missing.Count
        );
        foreach (var plan in missing)
        {
            logger.LogWarning(
                "RA-784 {Mode} report: not on the register: org {OrgNumber} {OrganisationName}, registration "
                    + "{RegistrationNumber}, work item {WorkItemId} ({ApplicationReference})",
                mode,
                ReadOrgNumber(plan.Item.Payload),
                ReadString(plan.Item.Payload, OrganisationNameField),
                ReadString(plan.Item.Payload, RegistrationNumberField),
                plan.Item.Id,
                ReadString(plan.Item.Payload, ApplicationReferenceField)
            );
        }
    }

    // The end-of-run report, part 2: every register row no 2027 work item
    // matched.
    private void ReportUnusedRegisterRows(IReadOnlyList<Plan> plans, string mode)
    {
        var used = plans
            .Select(p => p.RegisterRegistrationNumber)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var unused = PublicRegister2026
            .ByRegistrationNumber.Where(r => !used.Contains(r.Key))
            .OrderBy(r => r.Value.OrgId)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .ToList();
        logger.LogInformation(
            "RA-784 {Mode} report: {Count} of {Total} register rows matched no 2027 work item "
                + "({Accredited} accredited, {RegisteredOnly} registered only)",
            mode,
            unused.Count,
            PublicRegister2026.ByRegistrationNumber.Count,
            unused.Count(r => r.Value.AccreditationNumber is not null),
            unused.Count(r => r.Value.AccreditationNumber is null)
        );
        foreach (var (registrationNumber, row) in unused)
        {
            logger.LogInformation(
                "RA-784 {Mode} report: register row not used: org {OrgId} {OrganisationName}, registration "
                    + "{RegistrationNumber}, 2026 accreditation {AccreditationNumber}",
                mode,
                row.OrgId,
                row.OrganisationName,
                registrationNumber,
                row.AccreditationNumber ?? "none (registered only)"
            );
        }
    }

    // Planned against actual: the outcomes are what the plan found; the writes
    // are what an APPLY run did with the Added ones (a dry run writes nothing).
    private void ReportSummary(IReadOnlyList<Plan> plans, IReadOnlyList<Write> writes, string mode)
    {
        var written = string.Join(
            ", ",
            Enum.GetValues<Write>().Select(w => $"{w}={writes.Count(x => x == w)}")
        );
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
                + "Last year's accreditation planned: {Outcomes}. Written: {Written}. Issued numbers: {Issued}",
            mode,
            plans.Count,
            outcomes,
            written,
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

    private static bool HoldsAValue(BsonDocument payload, string field) =>
        payload.TryGetValue(field, out var value) && !value.IsBsonNull;

    private static int? OrgNumberOf(BsonDocument? payload) =>
        payload is not null
        && payload.TryGetValue(OperatorOrgNumberField, out var value)
        && BsonWholeNumber.TryGet(value, 0, 999_999, out var number)
            ? number
            : null;

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
