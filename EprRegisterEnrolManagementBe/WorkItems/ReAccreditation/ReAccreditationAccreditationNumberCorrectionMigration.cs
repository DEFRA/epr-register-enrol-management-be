using System.Text.RegularExpressions;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-691 remediation: two approved 2027 renewals were issued accreditation
/// numbers with a NEW sequence segment instead of keeping the one from their
/// 2026 number (root cause RA-690, fixed separately). This one-off migration
/// rewrites <c>payload.accreditationId</c> on exactly those two work items and
/// records the correction in each item's audit log (Application history). It
/// touches nothing else.
///
/// <para><strong>Matching rule.</strong> The numeric organisation id the
/// ticket names (500527, 500105) exists in management-be only inside the
/// accreditation number's <c>{OrgId:D6}</c> segment —
/// <c>payload.operatorOrganisationId</c> is the ReEx organisation UUID on real
/// submissions. So each target is located by the identity segments of its
/// number: year, agency, organisation and material
/// (<c>^A27ER500527\d{4}PL$</c>) with any sequence. Exactly one work item must
/// carry that identity; then its current value decides the outcome:</para>
/// <list type="bullet">
///   <item>the incorrect number → correct it;</item>
///   <item>the corrected number → already done, skip (no write, no entry);</item>
///   <item>anything else, or zero / more than one candidate → ABORT the whole
///   run before writing anything. A second candidate (say, a second plastic
///   site for the same organisation) means the data is not what the ticket
///   describes, so a human decides rather than this guessing.</item>
/// </list>
///
/// <para><strong>Gates</strong> (mirroring
/// <see cref="ReAccreditationIsNewSiteCorrectionMigration"/>), all required
/// before a single write:</para>
/// <list type="number">
///   <item><see cref="EnabledConfigKey"/> — off by default; a no-op in every
///   environment until deliberately switched on. On its own it runs the dry
///   run, which logs <c>org: old → new</c> per target and writes nothing.</item>
///   <item><see cref="ApplyConfigKey"/> — writes the corrections.</item>
///   <item><see cref="SpotCheckConfirmedByConfigKey"/> — must name whoever
///   reviewed the dry-run output against the live records before Apply takes
///   effect; without it an Apply run logs an error and stays a dry run. Logged
///   with the applied run; the audit entry's details are fixed by the
///   management-fe/mgmt-tests contract (<c>from</c>, <c>to</c>,
///   <c>reason</c>), so it is not repeated there.</item>
/// </list>
///
/// <para>Each write is a single atomic update filtered on the item's id AND
/// the incorrect value (<see cref="IWorkItemPersistence.CorrectPayloadFieldAsync"/>),
/// setting the field and appending the audit entry together. No state change.
/// Earlier audit entries that quote the old number (accreditation-issued,
/// determination-made, publishing-enqueued) are history and stay as they are;
/// the new entry records the change after them.</para>
/// </summary>
internal sealed class ReAccreditationAccreditationNumberCorrectionMigration(
    IConfiguration configuration,
    ILogger<ReAccreditationAccreditationNumberCorrectionMigration> logger,
    TimeProvider? timeProvider = null
) : IWorkItemMigration
{
    public const string EnabledConfigKey = "Diagnostics:Ra691CorrectAccreditationNumbers";
    public const string SpotCheckConfirmedByConfigKey = "Diagnostics:Ra691SpotCheckConfirmedBy";
    public const string ApplyConfigKey = "Diagnostics:Ra691CorrectAccreditationNumbersApply";

    // Contract with management-fe (renders the details rows) and mgmt-tests
    // (asserts them on the Application history tab). Do not change one side.
    public const string AuditAction = "accreditation-number-corrected";
    public const string AuditActionDisplayName = "Accreditation number corrected";
    public const string AuditReason = "RA-691";
    public const string AuditCreatedBy = "migration";
    public const string AuditCreatedByName = "Migration: RA-691 accreditation number correction";

    /// <summary>One work item to correct, by organisation.</summary>
    internal sealed record Target(
        string OrganisationName,
        string OrganisationId,
        string Incorrect,
        string Corrected
    )
    {
        // A real-format number is A{yy}{agency:2}{org:6}{seq:4}{material:2}:
        // everything before the sequence, and the material after it, is the
        // identity; only the sequence differs between Incorrect and Corrected.
        private const int SequenceStart = 11;
        private const int SequenceLength = 4;

        public string IdentityPrefix => Incorrect[..SequenceStart];

        public bool HasIdentityOf(string? accreditationId) =>
            accreditationId is not null
            && Regex.IsMatch(
                accreditationId,
                "^"
                    + Regex.Escape(IdentityPrefix)
                    + $"\\d{{{SequenceLength}}}"
                    + Regex.Escape(Incorrect[(SequenceStart + SequenceLength)..])
                    + "$",
                RegexOptions.None,
                TimeSpan.FromMilliseconds(250)
            );
    }

    /// <summary>The two numbers RA-691 corrects. Nothing else is ever touched.</summary>
    internal static readonly IReadOnlyList<Target> Targets =
    [
        new("Infinity Plastic Solutions Ltd", "500527", "A27ER5005270322PL", "A27ER5005270246PL"),
        new("PLASGRAN LIMITED", "500105", "A27ER5001050321PL", "A27ER5001050197PL"),
    ];

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public string Name => "ReAccreditation: correct RA-691 incorrectly sequenced accreditation numbers";

    /// <summary>
    /// The audit entry a correction appends. Shared with the mgmt-tests
    /// fixture (<see cref="ReAccreditationTestOrganisationFixtures"/>) so the
    /// two cannot drift.
    /// </summary>
    internal static WorkItemAuditEntry BuildAuditEntry(string from, string to, DateTime createdAt) =>
        new()
        {
            Action = AuditAction,
            ActionDisplayName = AuditActionDisplayName,
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

    public async Task ApplyAsync(IWorkItemPersistence persistence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(persistence);

        if (!configuration.GetValue(EnabledConfigKey, false))
        {
            return;
        }

        var apply = configuration.GetValue(ApplyConfigKey, false);
        var confirmedBy = configuration.GetValue<string?>(SpotCheckConfirmedByConfigKey);
        if (apply && string.IsNullOrWhiteSpace(confirmedBy))
        {
            logger.LogError(
                "RA-691 accreditation number correction: {ApplyKey} is set but {SpotCheckKey} is not, "
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
                "RA-691 accreditation number correction starting. Mode=APPLY. Spot-check confirmed by {ConfirmedBy}.",
                confirmedBy
            );
        }
        else
        {
            logger.LogInformation(
                "RA-691 accreditation number correction starting. Mode=DRY RUN (nothing will be written)."
            );
        }

        // Plan every target before writing any: an abort on the second
        // target must not leave the first one corrected.
        var plans = new List<(Target Target, WorkItem Item)>();
        foreach (var target in Targets)
        {
            var plan = await PlanAsync(persistence, target, cancellationToken);
            if (plan.Outcome == Outcome.Abort)
            {
                logger.LogError(
                    "RA-691 ABORTED, nothing written: {Organisation} ({OrganisationId}): {Reason}",
                    target.OrganisationName,
                    target.OrganisationId,
                    plan.Reason
                );
                return;
            }

            if (plan.Outcome == Outcome.AlreadyCorrected)
            {
                logger.LogInformation(
                    "RA-691 {Organisation} ({OrganisationId}): work item {WorkItemId} already carries "
                        + "{Corrected}; skipping.",
                    target.OrganisationName,
                    target.OrganisationId,
                    plan.Item!.Id,
                    target.Corrected
                );
                continue;
            }

            plans.Add((target, plan.Item!));
        }

        if (!apply)
        {
            foreach (var (target, item) in plans)
            {
                logger.LogInformation(
                    "RA-691 DRY RUN would correct {Organisation} ({OrganisationId}) work item "
                        + "{WorkItemId}: {From} → {To}",
                    target.OrganisationName,
                    target.OrganisationId,
                    item.Id,
                    target.Incorrect,
                    target.Corrected
                );
            }
            logger.LogInformation(
                "RA-691 DRY RUN complete. Would correct: {Count}.",
                plans.Count
            );
            return;
        }

        var corrected = 0;
        foreach (var (target, item) in plans)
        {
            var written = await persistence.CorrectPayloadFieldAsync(
                item.Id,
                ReAccreditationAccreditationNumber.PayloadKey,
                target.Incorrect,
                target.Corrected,
                BuildAuditEntry(target.Incorrect, target.Corrected, _timeProvider.GetUtcNow().UtcDateTime),
                cancellationToken
            );
            if (!written)
            {
                // Filtered on the incorrect value, so this only happens if
                // something changed the number between planning and now.
                // Nothing was written; the next run re-plans from fresh data.
                logger.LogWarning(
                    "RA-691 {Organisation} ({OrganisationId}): work item {WorkItemId} no longer "
                        + "carries {From}; nothing written.",
                    target.OrganisationName,
                    target.OrganisationId,
                    item.Id,
                    target.Incorrect
                );
                continue;
            }

            corrected++;
            logger.LogInformation(
                "RA-691 corrected {Organisation} ({OrganisationId}) work item {WorkItemId}: {From} → {To}",
                target.OrganisationName,
                target.OrganisationId,
                item.Id,
                target.Incorrect,
                target.Corrected
            );
        }

        logger.LogInformation("RA-691 APPLY complete. Corrected: {Count}.", corrected);
    }

    private enum Outcome
    {
        Correct,
        AlreadyCorrected,
        Abort,
    }

    private sealed record Plan(Outcome Outcome, WorkItem? Item = null, string? Reason = null);

    private static async Task<Plan> PlanAsync(
        IWorkItemPersistence persistence,
        Target target,
        CancellationToken cancellationToken
    )
    {
        var candidates = (
            await persistence.FindByPayloadFieldPrefixAsync(
                ReAccreditationType.Id,
                ReAccreditationAccreditationNumber.PayloadKey,
                target.IdentityPrefix,
                cancellationToken
            )
        )
            .Where(w => target.HasIdentityOf(ReAccreditationAccreditationNumber.Read(w.Payload)))
            .ToList();

        if (candidates.Count != 1)
        {
            var found = candidates.Count == 0
                ? "none"
                : string.Join(
                    ", ",
                    candidates.Select(c =>
                        $"{c.Id}={ReAccreditationAccreditationNumber.Read(c.Payload)}"
                    )
                );
            return new Plan(
                Outcome.Abort,
                Reason: $"expected exactly one work item with an accreditation number like "
                    + $"{target.IdentityPrefix}####{target.Incorrect[^2..]}, found {candidates.Count} ({found})."
            );
        }

        var item = candidates[0];
        var current = ReAccreditationAccreditationNumber.Read(item.Payload);
        if (current == target.Incorrect)
        {
            return new Plan(Outcome.Correct, item);
        }

        if (current == target.Corrected)
        {
            return new Plan(Outcome.AlreadyCorrected, item);
        }

        return new Plan(
            Outcome.Abort,
            item,
            $"work item {item.Id} carries {current}, which is neither {target.Incorrect} "
                + $"(incorrect) nor {target.Corrected} (corrected)."
        );
    }
}
