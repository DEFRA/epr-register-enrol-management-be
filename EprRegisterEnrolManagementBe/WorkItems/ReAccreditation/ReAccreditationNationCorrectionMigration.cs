using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-526 remediation: corrects <c>payload.nation</c> on work items that were routed by the
/// pre-RA-526 <c>ReAccreditationNationRoutingHook</c> — the version that read
/// <c>payload.siteAddress</c> expecting a nested BSON document with its own <c>.postcode</c>
/// field. Real submissions send <c>siteAddress</c> as a flat string instead, so that
/// derivation always returned null and every such work item was silently routed to England
/// regardless of its actual nation.
///
/// <para>
/// Unlike <see cref="ReAccreditationIsNewSiteCorrectionMigration"/>, this correction is not a
/// heuristic needing a human spot-check: <see cref="IReExAccreditationClient.GetNationAsync"/>
/// re-fetches the same authoritative source RA-526 itself uses at Seed time
/// (the registration's own <c>submittedToRegulator</c>, via <see cref="RegulatorNationMapper"/>),
/// so a corrected value is exactly what a fresh submission would have carried. It is still
/// gated behind <see cref="EnabledConfigKey"/> (off by default) and <see cref="ApplyConfigKey"/>
/// (dry run unless explicitly true) because it mutates already-submitted regulatory data with
/// payment-reference and regulator-assignment consequences — the report this migration's dry
/// run produces is meant to be reviewed before <see cref="ApplyConfigKey"/> is ever set.
/// </para>
///
/// <para>
/// Candidates are identified precisely, not guessed: only work items whose most recent
/// <c>routed-to-nation</c> audit entry has <c>details.derivedFrom == "site-address"</c> — the
/// literal value the pre-RA-526 hook always wrote, distinct from RA-526's own
/// <c>"submitted"</c>/<c>"default-england"</c> values — went through the broken code path.
/// Everything else is left untouched.
/// </para>
///
/// <para>
/// Idempotent: once corrected, an item's <c>routed-to-nation</c> entry is still the original
/// (unmodified, for history) <c>site-address</c> one, but a fresh ReEx lookup on a later run
/// would find <c>payload.nation</c> already matching and skip it — see
/// <see cref="PlanCorrection"/>.
/// </para>
/// </summary>
internal sealed class ReAccreditationNationCorrectionMigration(
    IReExAccreditationClient reExClient,
    IConfiguration configuration,
    ILogger<ReAccreditationNationCorrectionMigration> logger,
    TimeProvider? timeProvider = null
) : IWorkItemMigration
{
    public const string EnabledConfigKey = "Diagnostics:Ra526CorrectNation";
    public const string ApplyConfigKey = "Diagnostics:Ra526CorrectNationApply";

    public const string RoutedToNationAction = "routed-to-nation";
    public const string BrokenDerivedFromMarker = "site-address";

    public const string AuditAction = "nation-corrected";
    public const string AuditActionDisplayName = "Nation corrected";

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public string Name => "ReAccreditation: correct pre-RA-526 postcode-misrouted nation";

    public async Task ApplyAsync(IWorkItemPersistence persistence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(persistence);

        if (!configuration.GetValue(EnabledConfigKey, false))
        {
            return;
        }

        var apply = configuration.GetValue(ApplyConfigKey, false);
        logger.LogInformation(
            "RA-526 nation correction starting. Mode={Mode}.",
            apply ? "APPLY" : "DRY RUN (nothing will be written)");

        var corrected = 0;
        var alreadyCorrect = 0;
        var skippedNoIdentifiers = 0;
        var skippedLookupFailed = 0;
        var page = 1;

        while (true)
        {
            var result = await persistence.QueryAsync(
                new WorkItemQuery(
                    TypeIds: [ReAccreditationType.Id],
                    Page: page,
                    PageSize: WorkItemQuery.MaxPageSize,
                    IncludeArchived: true),
                cancellationToken);

            foreach (var candidate in result.Items)
            {
                // QueryAsync excludes AuditLog/Notes - fetch the full document before
                // inspecting the routed-to-nation entry or mutating.
                var full = await persistence.GetByIdAsync(candidate.Id, cancellationToken);
                if (full is null || !WasRoutedByBrokenDerivation(full))
                {
                    continue;
                }

                var plan = await PlanCorrection(full, cancellationToken);
                if (plan.Outcome == CorrectionOutcome.NoIdentifiers)
                {
                    skippedNoIdentifiers++;
                    logger.LogWarning(
                        "RA-526 correction skipped work item {WorkItemId}: missing "
                            + "operatorOrganisationId/operatorRegistrationId, cannot look up ReEx.",
                        full.Id);
                    continue;
                }
                if (plan.Outcome == CorrectionOutcome.LookupFailed)
                {
                    skippedLookupFailed++;
                    logger.LogWarning(
                        "RA-526 correction skipped work item {WorkItemId}: ReEx lookup failed "
                            + "or returned no result; will retry on the next run.",
                        full.Id);
                    continue;
                }
                if (plan.Outcome == CorrectionOutcome.AlreadyCorrect)
                {
                    alreadyCorrect++;
                    continue;
                }

                var (from, to) = (plan.From!, plan.To!);
                corrected++;

                if (!apply)
                {
                    logger.LogInformation(
                        "RA-526 DRY RUN would correct work item {WorkItemId} ref={ApplicationReference}: "
                            + "{From} -> {To}",
                        full.Id,
                        full.Payload.GetValue("applicationReference", "(none)"),
                        from,
                        to);
                    continue;
                }

                full.Payload["nation"] = to;
                full.AuditLog.Add(new WorkItemAuditEntry
                {
                    Action = AuditAction,
                    ActionDisplayName = AuditActionDisplayName,
                    CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
                    CreatedBy = "migration",
                    CreatedByName = "Migration: RA-526 nation correction",
                    Details = new Dictionary<string, string?>
                    {
                        ["issue"] = "RA-526",
                        ["reason"] =
                            "payload.nation was derived by the pre-RA-526 hook, which always "
                            + "defaulted to England on real submissions; corrected from the "
                            + "registration's own ReEx regulator.",
                        ["from"] = from,
                        ["to"] = to,
                    },
                });

                try
                {
                    await persistence.ReplaceAsync(full, cancellationToken);
                    logger.LogInformation(
                        "RA-526 corrected work item {WorkItemId}: {From} -> {To}",
                        full.Id,
                        from,
                        to);
                }
                catch (WorkItemConcurrencyException ex)
                {
                    logger.LogDebug(
                        ex,
                        "Concurrency conflict on work item {Id}; skipping - another instance "
                            + "already migrated it.",
                        full.Id);
                }
            }

            if (result.Items.Count < WorkItemQuery.MaxPageSize)
            {
                break;
            }

            page++;
        }

        logger.LogInformation(
            "RA-526 nation correction complete. Mode={Mode}. Items {Verb}: {Corrected}. "
                + "Already correct: {AlreadyCorrect}. Skipped (no identifiers): "
                + "{SkippedNoIdentifiers}. Skipped (ReEx lookup failed): {SkippedLookupFailed}.",
            apply ? "APPLY" : "DRY RUN",
            apply ? "corrected" : "that would be corrected",
            corrected,
            alreadyCorrect,
            skippedNoIdentifiers,
            skippedLookupFailed);
    }

    private static bool WasRoutedByBrokenDerivation(WorkItem item) =>
        item.AuditLog
            .LastOrDefault(e => e.Action == RoutedToNationAction)
            ?.Details
            ?.GetValueOrDefault("derivedFrom") == BrokenDerivedFromMarker;

    private enum CorrectionOutcome
    {
        NoIdentifiers,
        LookupFailed,
        AlreadyCorrect,
        Corrected,
    }

    /// <summary>
    /// The correction decision for one work item. <see cref="From"/>/<see cref="To"/> are only
    /// populated when <see cref="Outcome"/> is <see cref="CorrectionOutcome.Corrected"/>.
    /// </summary>
    private sealed record CorrectionPlan(CorrectionOutcome Outcome, string? From = null, string? To = null);

    // Planning is deliberately separate from the caller's apply/dry-run branch (mirrors
    // ReAccreditationIsNewSiteCorrectionMigration.PlanCorrections): the caller decides whether
    // to write, this decides only what the correct value is.
    private async Task<CorrectionPlan> PlanCorrection(WorkItem item, CancellationToken cancellationToken)
    {
        var organisationId = GetString(item.Payload, "operatorOrganisationId");
        var registrationId = GetString(item.Payload, "operatorRegistrationId");
        if (organisationId is null || registrationId is null)
        {
            return new CorrectionPlan(CorrectionOutcome.NoIdentifiers);
        }

        var correctNation = await reExClient.GetNationAsync(
            organisationId,
            registrationId,
            cancellationToken);
        if (correctNation is null)
        {
            return new CorrectionPlan(CorrectionOutcome.LookupFailed);
        }

        var currentNation = GetString(item.Payload, "nation") ?? Nation.England.ToString();
        var correctNationString = correctNation.Value.ToString();
        if (string.Equals(currentNation, correctNationString, StringComparison.OrdinalIgnoreCase))
        {
            return new CorrectionPlan(CorrectionOutcome.AlreadyCorrect);
        }

        return new CorrectionPlan(CorrectionOutcome.Corrected, currentNation, correctNationString);
    }

    private static string? GetString(BsonDocument payload, string key) =>
        payload.TryGetValue(key, out var value) && value.IsString ? value.AsString : null;
}
