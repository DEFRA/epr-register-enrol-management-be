using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-764 remediation: organisation 500069 changed its name on the Re/Ex side
/// of RREPW from <see cref="OldName"/> to <see cref="NewName"/>, and the new
/// name must show from 2027. Each 2027 application keeps the copy of the name
/// taken when it was started, so this one-off correction rewrites
/// <c>payload.organisationName</c> on that organisation's 2027 re-accreditation
/// work items. It is the only copy of the name in case management, and both
/// search paths read it (the Applications page's "Organisation name or ID"
/// substring match and the <c>$text</c> index, which Mongo maintains on the
/// update), so the renamed case is found by its new name.
///
/// <para><strong>Nothing else changes.</strong> No audit entry is written,
/// existing audit entries are not rewritten, and state, references, task data,
/// <see cref="WorkItem.LastModifiedAt"/> and assignment stay as they are (see
/// <see cref="IWorkItemPersistence.CompareAndSetPayloadFieldAsync"/>).</para>
///
/// <para><strong>Matching rule.</strong> Candidates are found by their exact
/// registration number (<see cref="RegistrationNumber"/>), not by name, and
/// only those with <c>accreditationYear</c> <see cref="AccreditationYear"/>
/// are in scope (any other year is left alone). Every 2027 candidate must also
/// carry <c>operatorOrgNumber</c> <see cref="OrganisationNumber"/> (when
/// present) and, once one is issued, a 2027 accreditation number for the same
/// organisation and material. Its name then decides the outcome: the old name
/// is renamed, the new name is already done (no write), and anything else, an
/// identity mismatch, or no 2027 candidate at all ABORTS the whole run before
/// any write. The 2026 accreditation number the ticket quotes
/// (<c>A26NR5000690003GL</c>) is not held on the 2027 case, so it cannot be
/// checked here.</para>
///
/// <para><strong>Gates</strong> (as RA-691, keys mirroring the operator
/// backend's RA-764 migration), all required before a single write:</para>
/// <list type="number">
///   <item><see cref="EnabledConfigKey"/>: off by default. On its own it is a
///   dry run that logs each planned rename and writes nothing.</item>
///   <item><see cref="ApplyConfigKey"/>: writes the renames.</item>
///   <item><see cref="SpotCheckConfirmedByConfigKey"/>: names whoever reviewed
///   the dry-run output; without it an Apply run logs an error and stays a dry
///   run.</item>
/// </list>
///
/// <para><strong>Why a hosted lifecycle service, not an
/// <see cref="IWorkItemMigration"/>.</strong> The migration runner starts
/// before <see cref="TestOrganisationFixtureHostedService"/>, which resets the
/// journey-test fixtures on every boot, so a rename made by the runner would
/// be undone on the e2e stack. <see cref="StartedAsync"/> runs after every
/// hosted service has started, so the RA-764 fixture is renamed after its
/// reset. Production runs exactly the same path; only configuration gates
/// it.</para>
/// </summary>
internal sealed class ReAccreditationOrganisationNameCorrectionMigration(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<ReAccreditationOrganisationNameCorrectionMigration> logger
) : IHostedLifecycleService
{
    public const string ConfigSection = "Migrations:Ra764OrganisationNameCorrection";
    public const string EnabledConfigKey = ConfigSection + ":Enabled";
    public const string ApplyConfigKey = ConfigSection + ":Apply";
    public const string SpotCheckConfirmedByConfigKey = ConfigSection + ":SpotCheckConfirmedBy";

    public const string OldName = "Encirc Ltd";
    public const string NewName = "VIDRALA UK & IRELAND LIMITED";
    public const int OrganisationNumber = 500069;
    public const string RegistrationNumber = "R26NR5000690003GL";
    public const int AccreditationYear = 2027;

    internal const string OrganisationNameField = "organisationName";
    internal const string RegistrationNumberField = "registrationNumber";
    internal const string OperatorOrgNumberField = "operatorOrgNumber";

    // A 2027 accreditation number for this organisation and material, any
    // sequence: A27 NR 500069 #### GL.
    private const string AccreditationNumberPrefix = "A27NR500069";
    private const string AccreditationNumberMaterial = "GL";
    private const int AccreditationNumberLength = 17;

    /// <summary>What one run did. Returned for tests; the logs say the same.</summary>
    internal enum RunResult
    {
        Disabled,
        Aborted,
        NothingToDo,
        DryRun,
        Applied,
    }

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
                "RA-764 organisation name correction failed; continuing startup. Will retry on next boot."
            );
        }
    }

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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

        var apply = ResolveApplyMode();

        var candidates = await persistence.FindByPayloadFieldAsync(
            ReAccreditationType.Id,
            RegistrationNumberField,
            RegistrationNumber,
            cancellationToken
        );

        // Plan every candidate before writing any: an abort on the second
        // must not leave the first renamed.
        var (toRename, abortReason) = Plan(candidates);
        if (abortReason is not null)
        {
            logger.LogError("RA-764 ABORTED, nothing written: {Reason}", abortReason);
            return RunResult.Aborted;
        }

        if (toRename.Count == 0)
        {
            logger.LogInformation("RA-764 complete. Nothing to rename.");
            return RunResult.NothingToDo;
        }

        if (!apply)
        {
            LogDryRun(toRename);
            return RunResult.DryRun;
        }

        await RenameAsync(persistence, toRename, cancellationToken);
        return RunResult.Applied;
    }

    /// <summary>
    /// True for an applied run. Apply without a spot-check name falls back to
    /// a dry run, loudly.
    /// </summary>
    private bool ResolveApplyMode()
    {
        var apply = configuration.GetValue(ApplyConfigKey, false);
        var confirmedBy = configuration.GetValue<string?>(SpotCheckConfirmedByConfigKey);
        if (apply && string.IsNullOrWhiteSpace(confirmedBy))
        {
            logger.LogError(
                "RA-764 organisation name correction: {ApplyKey} is set but {SpotCheckKey} is not, "
                    + "so running as a DRY RUN. Set it to the name of whoever reviewed the dry-run "
                    + "output against the live records.",
                ApplyConfigKey,
                SpotCheckConfirmedByConfigKey
            );
            apply = false;
        }

        if (apply)
        {
            logger.LogInformation(
                "RA-764 organisation name correction starting. Mode=APPLY. Spot-check confirmed by {ConfirmedBy}.",
                confirmedBy
            );
        }
        else
        {
            logger.LogInformation(
                "RA-764 organisation name correction starting. Mode=DRY RUN (nothing will be written)."
            );
        }

        return apply;
    }

    private enum Decision
    {
        OtherYear,
        AlreadyRenamed,
        Rename,
    }

    /// <summary>
    /// The work items to rename, or why the whole run must abort. Writes
    /// nothing.
    /// </summary>
    private (List<WorkItem> ToRename, string? AbortReason) Plan(IReadOnlyList<WorkItem> candidates)
    {
        var toRename = new List<WorkItem>();
        var inScope = 0;
        foreach (var item in candidates)
        {
            var (decision, abortReason) = Decide(item);
            if (abortReason is not null)
            {
                return (toRename, abortReason);
            }

            if (decision == Decision.OtherYear)
            {
                continue;
            }

            inScope++;
            if (decision == Decision.Rename)
            {
                toRename.Add(item);
            }
        }

        return inScope == 0
            ? (
                toRename,
                $"expected at least one {AccreditationYear} work item with registration number "
                    + $"{RegistrationNumber}, found none ({candidates.Count} of any year)."
            )
            : (toRename, null);
    }

    /// <summary>What to do with one candidate, or why the run must abort.</summary>
    private (Decision Decision, string? AbortReason) Decide(WorkItem item)
    {
        if (!TryReadYear(item.Payload, out var year))
        {
            return (default, $"work item {item.Id} has no usable accreditationYear.");
        }

        if (year != AccreditationYear)
        {
            logger.LogInformation(
                "RA-764 work item {WorkItemId} is a {Year} application; left alone.",
                item.Id,
                year
            );
            return (Decision.OtherYear, null);
        }

        var mismatch = IdentityMismatch(item);
        if (mismatch is not null)
        {
            return (default, $"work item {item.Id} {mismatch}");
        }

        var name = ReadString(item.Payload, OrganisationNameField);
        if (name == NewName)
        {
            logger.LogInformation(
                "RA-764 work item {WorkItemId} already carries {NewName}; skipping.",
                item.Id,
                NewName
            );
            return (Decision.AlreadyRenamed, null);
        }

        return name == OldName
            ? (Decision.Rename, null)
            : (
                default,
                $"work item {item.Id} carries organisation name '{name}', which is neither "
                    + $"'{OldName}' (old) nor '{NewName}' (new)."
            );
    }

    private void LogDryRun(List<WorkItem> toRename)
    {
        foreach (var item in toRename)
        {
            logger.LogInformation(
                "RA-764 DRY RUN would rename work item {WorkItemId} ({ApplicationReference}): "
                    + "{From} → {To}",
                item.Id,
                ReadString(item.Payload, "applicationReference"),
                OldName,
                NewName
            );
        }
        logger.LogInformation("RA-764 DRY RUN complete. Would rename: {Count}.", toRename.Count);
    }

    private async Task RenameAsync(
        IWorkItemPersistence persistence,
        List<WorkItem> toRename,
        CancellationToken cancellationToken
    )
    {
        var renamed = 0;
        foreach (var item in toRename)
        {
            var written = await persistence.CompareAndSetPayloadFieldAsync(
                item.Id,
                OrganisationNameField,
                OldName,
                NewName,
                cancellationToken
            );
            if (!written)
            {
                // Filtered on the old name, so this only happens if something
                // changed it between planning and now. Nothing was written;
                // the next run re-plans from fresh data.
                logger.LogWarning(
                    "RA-764 work item {WorkItemId} no longer carries {From}; nothing written.",
                    item.Id,
                    OldName
                );
                continue;
            }

            renamed++;
            logger.LogInformation(
                "RA-764 renamed work item {WorkItemId} ({ApplicationReference}): {From} → {To}",
                item.Id,
                ReadString(item.Payload, "applicationReference"),
                OldName,
                NewName
            );
        }

        logger.LogInformation("RA-764 APPLY complete. Renamed: {Count}.", renamed);
    }

    /// <summary>
    /// Why <paramref name="item"/> is not organisation 500069's 2027 glass
    /// application, or <c>null</c> when it is. Older submissions predate
    /// <c>operatorOrgNumber</c>, and the accreditation number exists only once
    /// the 2027 accreditation is issued, so each is checked only when present.
    /// </summary>
    private static string? IdentityMismatch(WorkItem item)
    {
        if (
            item.Payload.TryGetValue(OperatorOrgNumberField, out var orgNumber)
            && !orgNumber.IsBsonNull
            && !(
                BsonWholeNumber.TryGet(orgNumber, 0, 999_999, out var number)
                && number == OrganisationNumber
            )
        )
        {
            return $"carries operatorOrgNumber {orgNumber}, not {OrganisationNumber}.";
        }

        var accreditationId = ReAccreditationAccreditationNumber.Read(item.Payload);
        if (accreditationId is not null && !IsThisOrganisationsAccreditationNumber(accreditationId))
        {
            return $"carries accreditation number {accreditationId}, which is not "
                + $"{AccreditationNumberPrefix}####{AccreditationNumberMaterial}.";
        }

        return null;
    }

    private static bool IsThisOrganisationsAccreditationNumber(string value) =>
        value.Length == AccreditationNumberLength
        && value.StartsWith(AccreditationNumberPrefix, StringComparison.Ordinal)
        && value.EndsWith(AccreditationNumberMaterial, StringComparison.Ordinal)
        && value[AccreditationNumberPrefix.Length..^AccreditationNumberMaterial.Length].All(char.IsAsciiDigit);

    private static bool TryReadYear(BsonDocument payload, out int year)
    {
        year = 0;
        return payload.TryGetValue("accreditationYear", out var value)
            && BsonWholeNumber.TryGet(value, 2000, 2999, out year);
    }

    private static string? ReadString(BsonDocument payload, string field) =>
        payload.TryGetValue(field, out var value) && value.IsString ? value.AsString : null;
}
