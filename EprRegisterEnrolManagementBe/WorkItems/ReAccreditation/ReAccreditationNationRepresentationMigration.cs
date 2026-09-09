using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-551 remediation: corrects <c>payload.nation</c> on work items where it was
/// silently rewritten from a string to its BSON ordinal int. <see cref="Models.Nation"/>
/// on <see cref="Models.ReAccreditationPayload"/> was missing
/// <c>[BsonRepresentation(BsonType.String)]</c> (unlike its sibling
/// <see cref="Models.ReAccreditationPayload.GlassRecyclingProcess"/>), so every
/// deserialize → with-mutate → <c>ToBsonDocument()</c> → merge cycle in
/// <c>ReAccreditationApprovalService</c> / <c>ReAccreditationDulyMakingService</c>
/// re-wrote the field as the driver's default ordinal int
/// (England=0, Scotland=1, Wales=2, NorthernIreland=3) instead of the enum member
/// name. The payload starts out correct — a raw BSON string written by
/// <c>ReAccreditationNationRoutingHook</c> at submission, which bypasses the typed
/// model entirely — so only items that have since been duly-made or approved are
/// affected.
///
/// <para>
/// Once corrupted to an int, <c>payload.nation</c> never matches the string-based
/// <c>{"payload.nation": {"$in": ["England", ...]}}</c> filter every nation-scoped
/// worklist query builds (<c>WorkItemQueryBinding.ReadNations</c> /
/// <c>WorkItemPersistence.BuildFilter</c>), so the affected item silently
/// disappears from every nation-filtered worklist.
/// </para>
///
/// <para>
/// Candidates are identified precisely by the stored BSON type of
/// <c>payload.nation</c> being <see cref="BsonType.Int32"/> — not by any audit-log
/// marker (that mechanism belongs to the unrelated RA-526 <c>nation-corrected</c>
/// migration, <see cref="ReAccreditationNationCorrectionMigration"/>, which fixes a
/// different bug: a wrong nation value, not a wrong BSON representation of a value
/// that was already correct). The int's ordinal is mapped back onto the
/// <see cref="Models.Nation"/> enum's declared member order to recover the original
/// string.
/// </para>
///
/// <para>
/// Idempotent and unconditional, like every other backfill migration in this file
/// family: it runs on every boot via <see cref="WorkItemMigrationHostedService"/>,
/// and a repeat run against an already-corrected item finds
/// <c>payload.nation</c> already a <see cref="BsonType.String"/> and skips it, so no
/// enable flag or dry-run mode is needed. It appends its own
/// <see cref="AuditAction"/> audit entry per correction — distinct from RA-526's
/// <c>nation-corrected</c> — so a corrected item's history records exactly what
/// changed and why, visible in the case management UI.
/// </para>
/// </summary>
internal sealed class ReAccreditationNationRepresentationMigration(
    ILogger<ReAccreditationNationRepresentationMigration> logger,
    TimeProvider? timeProvider = null
) : IWorkItemMigration
{
    public const string AuditAction = "nation-representation-corrected";
    public const string AuditActionDisplayName = "Nation representation corrected";

    // Declared order of the Nation enum (Models/Nation.cs) - confirmed by reading that
    // file, not assumed. The BSON int representation the driver wrote (pre-fix) is the
    // enum's ordinal, so this array index-maps an ordinal straight back onto the member name.
    private static readonly string[] s_nationNamesByOrdinal =
    [
        nameof(Nation.England),
        nameof(Nation.Scotland),
        nameof(Nation.Wales),
        nameof(Nation.NorthernIreland),
    ];

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public string Name => "ReAccreditation: correct RA-551 int-represented nation back to string";

    public async Task ApplyAsync(IWorkItemPersistence persistence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(persistence);

        logger.LogInformation("RA-551 nation representation correction starting.");

        var tally = new CorrectionTally();
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
                await ProcessCandidateAsync(candidate, persistence, tally, cancellationToken);
            }

            if (result.Items.Count < WorkItemQuery.MaxPageSize)
            {
                break;
            }

            page++;
        }

        logger.LogInformation(
            "RA-551 nation representation correction complete. Corrected: {Corrected}. "
                + "Already correct: {AlreadyCorrect}.",
            tally.Corrected,
            tally.AlreadyCorrect);
    }

    /// <summary>Running totals for the completion log — mutated in place by <see cref="ProcessCandidateAsync"/>.</summary>
    private sealed class CorrectionTally
    {
        public int Corrected;
        public int AlreadyCorrect;
    }

    private async Task ProcessCandidateAsync(
        WorkItem candidate,
        IWorkItemPersistence persistence,
        CorrectionTally tally,
        CancellationToken cancellationToken)
    {
        // QueryAsync excludes AuditLog/Notes - fetch the full document before mutating.
        var full = await persistence.GetByIdAsync(candidate.Id, cancellationToken);
        if (full is null || !TryGetLegacyIntNation(full, out var ordinal))
        {
            tally.AlreadyCorrect++;
            return;
        }

        if (ordinal < 0 || ordinal >= s_nationNamesByOrdinal.Length)
        {
            logger.LogWarning(
                "RA-551 correction skipped work item {WorkItemId}: payload.nation is an "
                    + "int ({Ordinal}) outside the known Nation enum range; leaving untouched "
                    + "for manual investigation.",
                full.Id,
                ordinal);
            return;
        }

        var from = ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var to = s_nationNamesByOrdinal[ordinal];
        await ApplyCorrectionAsync(full, from, to, persistence, cancellationToken);
        tally.Corrected++;
    }

    private async Task ApplyCorrectionAsync(
        WorkItem item,
        string from,
        string to,
        IWorkItemPersistence persistence,
        CancellationToken cancellationToken)
    {
        item.Payload["nation"] = to;
        item.AuditLog.Add(new WorkItemAuditEntry
        {
            Action = AuditAction,
            ActionDisplayName = AuditActionDisplayName,
            CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
            CreatedBy = "migration",
            CreatedByName = "Migration: RA-551 nation representation correction",
            Details = new Dictionary<string, string?>
            {
                ["issue"] = "RA-551",
                ["reason"] =
                    "payload.nation was silently rewritten from a string to its BSON "
                    + "ordinal int by a deserialize/ToBsonDocument() cycle missing "
                    + "[BsonRepresentation(BsonType.String)]; restored to the original "
                    + "string using the Nation enum's declared member order.",
                ["from"] = from,
                ["to"] = to,
            },
        });

        try
        {
            await persistence.ReplaceAsync(item, cancellationToken);
            logger.LogInformation(
                "RA-551 corrected work item {WorkItemId}: {From} -> {To}",
                item.Id,
                from,
                to);
        }
        catch (WorkItemConcurrencyException ex)
        {
            logger.LogDebug(
                ex,
                "Concurrency conflict on work item {Id}; skipping - another instance "
                    + "already migrated it.",
                item.Id);
        }
    }

    private static bool TryGetLegacyIntNation(WorkItem item, out int ordinal)
    {
        ordinal = 0;
        if (!item.Payload.TryGetValue("nation", out var value) || value.BsonType != BsonType.Int32)
        {
            return false;
        }

        ordinal = value.AsInt32;
        return true;
    }
}
