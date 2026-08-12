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
/// Two of the three sorts cannot be expressed as a plain field sort:
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
    /// blank or unrecognised — in which case the caller keeps the default
    /// newest-submitted-first Find path, so the default ordering can never
    /// regress. <paramref name="descendingOverride"/> is the optional
    /// <c>?dir=</c> value; when <c>null</c> each column applies its natural
    /// default direction.
    /// </summary>
    internal static (BsonDocument? AddFields, BsonDocument Sort)? BuildStages(
        string? sortToken,
        bool? descendingOverride,
        IReadOnlyDictionary<string, int> statusRank)
    {
        ArgumentNullException.ThrowIfNull(statusRank);

        return sortToken?.Trim().ToLowerInvariant() switch
        {
            OrganisationToken => BuildOrganisationStages(descendingOverride),
            StatusToken => BuildStatusStages(descendingOverride, statusRank),
            DueDateToken => BuildDueDateStages(descendingOverride),
            _ => null
        };
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
