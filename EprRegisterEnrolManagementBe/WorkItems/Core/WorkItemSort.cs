using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Translates the <c>?sort=</c> / <c>?dir=</c> query params into the MongoDB
/// aggregation stages that reorder the Applications list (RA-324). Kept as a
/// pure, side-effect-free helper so the tricky bits — the SLA due-date
/// expression and the status workflow ranking — can be unit-tested without a
/// database.
///
/// <para>
/// RA-751 adds <see cref="LastModifiedToken"/>, a plain field sort with no
/// <c>$addFields</c> stage, which the persistence layer runs on the Find path.
/// </para>
///
/// <para>
/// Two of the three RA-324 sorts cannot be expressed as a plain field sort:
/// <list type="bullet">
/// <item><description><b>Due date</b> is not stored; it is
/// <c>slaClock.startedAt + slaClock.targetDuration</c>. Both operands are
/// persisted (targetDuration as .NET ticks), so the deadline is computed in an
/// <c>$addFields</c> stage — no persisted field or migration needed. Items
/// whose SLA clock has not started (pre-payment) have no deadline and are
/// pushed to the end regardless of direction.</description></item>
/// <item><description><b>Status</b> sorts by workflow progression, not
/// alphabetically, so state ids are mapped to a rank via <c>$switch</c>.</description></item>
/// </list>
/// </para>
/// </summary>
internal static class WorkItemSort
{
    internal const string OrganisationToken = "organisation";
    internal const string StatusToken = "status";
    internal const string DueDateToken = "due-date";

    /// <summary>
    /// RA-751 AC8 ("as in RA-740": most recently updated first): sorts on
    /// <see cref="WorkItem.LastModifiedAt"/>. Descending (newest first) unless
    /// <c>?dir=asc</c>. A plain field sort, so it needs no computed field and
    /// <see cref="WorkItemPersistence.QueryAsync"/> runs it on the Find path.
    /// </summary>
    internal const string LastModifiedToken = "last-modified";

    // Computed fields injected by an $addFields stage. They must be removed
    // ($unset) before the aggregation result is deserialised because WorkItem
    // does not ignore extra BSON elements.
    internal const string DeadlineField = "_sortDeadline";
    internal const string HasDeadlineField = "_sortHasDeadline";
    internal const string RankField = "_sortRank";
    internal const string OrgField = "_sortOrg";

    /// <summary>Every computed field name this helper can inject; always $unset before deserialising.</summary>
    internal static readonly IReadOnlyList<string> ComputedFields =
        [DeadlineField, HasDeadlineField, RankField, OrgField];

    // Rank assigned to any state id the registry does not know about, so
    // unexpected states sort last under a Status sort rather than colliding
    // with rank 0.
    internal const int UnknownStateRank = int.MaxValue;

    /// <summary>
    /// RA-751: the tokens <see cref="BuildStages"/> understands. The endpoint
    /// rejects any other non-blank <c>?sort=</c> value with 400 rather than
    /// quietly falling back to the default order, so a mistyped or renamed
    /// token can never show results in an order the caller did not ask for.
    /// </summary>
    internal static IReadOnlyCollection<string> Tokens => s_stageBuilders.Keys;

    // The single list of sort tokens and how each builds its stages; Tokens,
    // IsKnownToken and BuildStages all read it, so a new sort is one entry.
    private static readonly Dictionary<
        string,
        Func<bool?, IReadOnlyDictionary<string, int>, (BsonDocument?, BsonDocument)>
    > s_stageBuilders = new(StringComparer.Ordinal)
    {
        [OrganisationToken] = (descending, _) => BuildOrganisationStages(descending),
        [StatusToken] = BuildStatusStages,
        [DueDateToken] = (descending, _) => BuildDueDateStages(descending),
        [LastModifiedToken] = (descending, _) => BuildLastModifiedStages(descending),
    };

    /// <summary>Whether <paramref name="sortToken"/> names a sort, matched as <see cref="BuildStages"/> matches it.</summary>
    internal static bool IsKnownToken(string sortToken) =>
        s_stageBuilders.ContainsKey(Normalise(sortToken));

    private static string Normalise(string sortToken) => sortToken.Trim().ToLowerInvariant();

    /// <summary>
    /// Map each state id to a workflow rank derived from the order states are
    /// declared in on their <see cref="IWorkItemType"/>. Mirrors
    /// <see cref="TerminalStates.Ids"/>: data-driven from the registry rather
    /// than hardcoded, so a module's declared state order IS its sort order.
    /// First declaration wins when types share an id.
    /// </summary>
    internal static IReadOnlyDictionary<string, int> StatusRank(IWorkItemRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var next = 0;
        foreach (var type in registry.Types)
        {
            foreach (var state in type.States)
            {
                if (!rank.ContainsKey(state.Id))
                {
                    rank[state.Id] = next++;
                }
            }
        }
        return rank;
    }

    /// <summary>
    /// Build the <c>$addFields</c> (may be <c>null</c>) and <c>$sort</c> stages
    /// for the requested <c>sort</c> token, or <c>null</c> when the token is
    /// blank, in which case the caller keeps the default newest-submitted-first
    /// Find path. An unrecognised non-blank token throws: the endpoint rejects
    /// it with 400 first (<see cref="WorkItemQueryBinding.Validate"/>), so
    /// reaching here with one is a defect, never a reason to pick an order the
    /// caller did not ask for. <paramref name="descendingOverride"/> is the
    /// optional <c>?dir=</c> value; when <c>null</c> each column applies its
    /// natural default direction.
    /// </summary>
    internal static (BsonDocument? AddFields, BsonDocument Sort)? BuildStages(
        string? sortToken,
        bool? descendingOverride,
        IReadOnlyDictionary<string, int> statusRank)
    {
        ArgumentNullException.ThrowIfNull(statusRank);

        if (string.IsNullOrWhiteSpace(sortToken))
        {
            return null;
        }

        return s_stageBuilders.TryGetValue(Normalise(sortToken), out var build)
            ? build(descendingOverride, statusRank)
            : throw new ArgumentException($"Unknown sort token '{sortToken}'.", nameof(sortToken));
    }

    private static (BsonDocument?, BsonDocument) BuildOrganisationStages(bool? descendingOverride)
    {
        // Default A→Z. Case-insensitive by lower-casing so "acme" and "Acme"
        // sort together; a missing name becomes "" (sorts first ascending).
        var dir = descendingOverride == true ? -1 : 1;
        var addFields = new BsonDocument(OrgField,
            new BsonDocument("$toLower",
                new BsonDocument("$ifNull", new BsonArray { "$payload.organisationName", "" })));
        var sort = StableSort(OrgField, dir);
        return (addFields, sort);
    }

    private static (BsonDocument?, BsonDocument) BuildStatusStages(
        bool? descendingOverride, IReadOnlyDictionary<string, int> statusRank)
    {
        // Default = workflow order (ascending rank).
        var dir = descendingOverride == true ? -1 : 1;
        var branches = new BsonArray(statusRank
            .OrderBy(kv => kv.Value)
            .Select(kv => new BsonDocument
            {
                { "case", new BsonDocument("$eq", new BsonArray { "$stateId", kv.Key }) },
                { "then", kv.Value }
            }));
        var addFields = new BsonDocument(RankField,
            new BsonDocument("$switch", new BsonDocument
            {
                { "branches", branches },
                { "default", UnknownStateRank }
            }));
        var sort = StableSort(RankField, dir);
        return (addFields, sort);
    }

    private static (BsonDocument?, BsonDocument) BuildLastModifiedStages(bool? descendingOverride)
    {
        // Default newest first; only an explicit ?dir=asc reverses it. Ties
        // break on _id in the same direction, so paging is stable and the
        // { lastModifiedAt: -1, _id: -1 } index serves both directions.
        var dir = descendingOverride == false ? 1 : -1;
        var sort = new BsonDocument
        {
            { "lastModifiedAt", dir },
            { "_id", dir }
        };
        return (null, sort);
    }

    private static (BsonDocument?, BsonDocument) BuildDueDateStages(bool? descendingOverride)
    {
        // Default = soonest deadline first (ascending). deadline =
        // slaClock.startedAt + targetDuration(ticks); ticks→ms via /TicksPerMillisecond
        // because Mongo date arithmetic is in milliseconds. Items with no clock
        // started have no deadline and are forced last in BOTH directions by
        // sorting the has-deadline flag descending first.
        var dir = descendingOverride == true ? -1 : 1;
        // 1 only when the SLA clock has actually started (a real date). $type
        // distinguishes a genuine date from BOTH a missing field and an
        // explicit null — unlike $ne against null, which reports a missing
        // field as "not null" and would wrongly flag a clock-less item as
        // having a deadline.
        var hasDeadline = new BsonDocument("$cond", new BsonArray
        {
            new BsonDocument("$eq", new BsonArray
            {
                new BsonDocument("$type", "$slaClock.startedAt"),
                "date"
            }),
            1,
            0
        });
        var deadline = new BsonDocument("$add", new BsonArray
        {
            "$slaClock.startedAt",
            new BsonDocument("$divide",
                new BsonArray { "$slaClock.targetDuration", TimeSpan.TicksPerMillisecond })
        });
        var addFields = new BsonDocument
        {
            { HasDeadlineField, hasDeadline },
            { DeadlineField, deadline }
        };
        var sort = new BsonDocument
        {
            { HasDeadlineField, -1 },
            { DeadlineField, dir },
            { "submittedAt", -1 },
            { "_id", 1 }
        };
        return (addFields, sort);
    }

    /// <summary>
    /// A sort on <paramref name="primaryField"/> made deterministic by breaking
    /// ties on newest-submitted then <c>_id</c>, so paging is stable.
    /// </summary>
    private static BsonDocument StableSort(string primaryField, int direction) => new()
    {
        { primaryField, direction },
        { "submittedAt", -1 },
        { "_id", 1 }
    };
}
