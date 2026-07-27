using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-324: unit coverage for <see cref="WorkItemSort"/>, the pure helper that
/// turns the <c>?sort=</c> / <c>?dir=</c> params into aggregation stages. The
/// tricky expressions (SLA due-date arithmetic, status workflow ranking) are
/// asserted here without a database.
/// </summary>
public class WorkItemSortTests
{
    private static readonly IReadOnlyDictionary<string, int> s_rank =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["submitted"] = 0,
            ["duly-made"] = 1,
            ["approved"] = 2
        };

    private static IWorkItemRegistry Registry(params IWorkItemType[] types) =>
        new WorkItemRegistry(types);

    // ─────────────────────────────── StatusRank ──────────────────────────────

    [Fact]
    public void StatusRank_ranks_states_in_declaration_order()
    {
        var registry = Registry(new TestWorkItemType("t1", "T1", states: new[]
        {
            new WorkItemState("submitted", "Submitted"),
            new WorkItemState("duly-made", "Duly made"),
            new WorkItemState("approved", "Approved", IsTerminal: true)
        }));

        var rank = WorkItemSort.StatusRank(registry);

        Assert.Equal(0, rank["submitted"]);
        Assert.Equal(1, rank["duly-made"]);
        Assert.Equal(2, rank["approved"]);
    }

    [Fact]
    public void StatusRank_is_case_insensitive()
    {
        var registry = Registry(new TestWorkItemType("t1", "T1", states: new[]
        {
            new WorkItemState("submitted", "Submitted")
        }));

        var rank = WorkItemSort.StatusRank(registry);

        Assert.Equal(0, rank["SUBMITTED"]);
    }

    [Fact]
    public void StatusRank_first_declaration_wins_across_types()
    {
        var registry = Registry(
            new TestWorkItemType("t1", "T1", states: new[]
            {
                new WorkItemState("submitted", "Submitted"),
                new WorkItemState("shared", "Shared")
            }),
            new TestWorkItemType("t2", "T2", states: new[]
            {
                new WorkItemState("shared", "Shared"),
                new WorkItemState("other", "Other")
            }));

        var rank = WorkItemSort.StatusRank(registry);

        // 'shared' keeps the rank from its first (t1) appearance, and 'other'
        // continues the global counter rather than resetting per type.
        Assert.Equal(1, rank["shared"]);
        Assert.Equal(2, rank["other"]);
    }

    [Fact]
    public void StatusRank_throws_on_null_registry()
    {
        Assert.Throws<ArgumentNullException>(() => WorkItemSort.StatusRank(null!));
    }

    // ─────────────────────────────── BuildStages ─────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("submittedAt")]
    [InlineData("nonsense")]
    public void BuildStages_returns_null_for_default_or_unknown_sort(string? token)
    {
        Assert.Null(WorkItemSort.BuildStages(token, descendingOverride: null, s_rank));
    }

    [Theory]
    [InlineData("  Due-Date  ", WorkItemSort.DeadlineField)]
    [InlineData("ORGANISATION", WorkItemSort.OrgField)]
    [InlineData("Status", WorkItemSort.RankField)]
    public void BuildStages_normalises_token_casing_and_whitespace(string token, string expectedComputedField)
    {
        // Trimmed + lower-cased before matching, and — crucially — resolves to
        // the INTENDED column: assert an identifying computed field of that
        // column's $addFields stage, not merely that some stage came back.
        var stages = WorkItemSort.BuildStages(token, descendingOverride: null, s_rank);

        Assert.NotNull(stages);
        Assert.True(
            stages!.Value.AddFields!.Contains(expectedComputedField),
            $"Token '{token}' should resolve to the stage adding '{expectedComputedField}'.");
    }

    [Fact]
    public void BuildStages_throws_on_null_status_rank()
    {
        Assert.Throws<ArgumentNullException>(() =>
            WorkItemSort.BuildStages("status", descendingOverride: null, null!));
    }

    [Fact]
    public void BuildStages_organisation_lowercases_and_sorts_ascending_by_default()
    {
        var stages = WorkItemSort.BuildStages("organisation", descendingOverride: null, s_rank);

        Assert.NotNull(stages);
        var (addFields, sort) = stages!.Value;
        // _sortOrg = $toLower($ifNull(payload.organisationName, ""))
        var ifNull = addFields![WorkItemSort.OrgField]["$toLower"]["$ifNull"].AsBsonArray;
        Assert.Equal("$payload.organisationName", ifNull[0].AsString);
        Assert.Equal("", ifNull[1].AsString);
        Assert.Equal(1, sort[WorkItemSort.OrgField].AsInt32);
        // Stable tiebreak.
        Assert.Equal(-1, sort["submittedAt"].AsInt32);
        Assert.Equal(1, sort["_id"].AsInt32);
    }

    [Fact]
    public void BuildStages_organisation_descending_flips_only_the_primary_key()
    {
        var stages = WorkItemSort.BuildStages("organisation", descendingOverride: true, s_rank);

        var (_, sort) = stages!.Value;
        Assert.Equal(-1, sort[WorkItemSort.OrgField].AsInt32);
        Assert.Equal(-1, sort["submittedAt"].AsInt32);
    }

    [Fact]
    public void BuildStages_status_builds_switch_branches_in_rank_order()
    {
        var stages = WorkItemSort.BuildStages("status", descendingOverride: null, s_rank);

        var (addFields, sort) = stages!.Value;
        var switchDoc = addFields![WorkItemSort.RankField]["$switch"].AsBsonDocument;
        var branches = switchDoc["branches"].AsBsonArray;
        Assert.Equal(3, branches.Count);
        // Branches ordered by ascending rank: submitted(0), duly-made(1), approved(2).
        Assert.Equal("$stateId", branches[0]["case"]["$eq"][0].AsString);
        Assert.Equal("submitted", branches[0]["case"]["$eq"][1].AsString);
        Assert.Equal(0, branches[0]["then"].AsInt32);
        Assert.Equal("duly-made", branches[1]["case"]["$eq"][1].AsString);
        Assert.Equal(2, branches[2]["then"].AsInt32);
        // Unknown states fall to the far end.
        Assert.Equal(int.MaxValue, switchDoc["default"].AsInt32);
        Assert.Equal(1, sort[WorkItemSort.RankField].AsInt32);
    }

    [Fact]
    public void BuildStages_status_descending_flips_rank_direction()
    {
        var stages = WorkItemSort.BuildStages("status", descendingOverride: true, s_rank);

        var (_, sort) = stages!.Value;
        Assert.Equal(-1, sort[WorkItemSort.RankField].AsInt32);
    }

    [Fact]
    public void BuildStages_due_date_computes_deadline_from_start_plus_target()
    {
        var stages = WorkItemSort.BuildStages("due-date", descendingOverride: null, s_rank);

        var (addFields, sort) = stages!.Value;

        // hasDeadline = ($type(slaClock.startedAt) == "date") ? 1 : 0 — so a
        // missing OR null clock both yield 0.
        var cond = addFields![WorkItemSort.HasDeadlineField]["$cond"].AsBsonArray;
        var eq = cond[0]["$eq"].AsBsonArray;
        Assert.Equal("$slaClock.startedAt", eq[0]["$type"].AsString);
        Assert.Equal("date", eq[1].AsString);
        Assert.Equal(1, cond[1].AsInt32);
        Assert.Equal(0, cond[2].AsInt32);

        // deadline = startedAt + (targetDuration ticks / 10000 ms)
        var add = addFields[WorkItemSort.DeadlineField]["$add"].AsBsonArray;
        Assert.Equal("$slaClock.startedAt", add[0].AsString);
        var divide = add[1]["$divide"].AsBsonArray;
        Assert.Equal("$slaClock.targetDuration", divide[0].AsString);
        Assert.Equal(TimeSpan.TicksPerMillisecond, divide[1].AsInt64);

        // No-clock items forced last (hasDeadline desc), then soonest first (asc).
        Assert.Equal(-1, sort[WorkItemSort.HasDeadlineField].AsInt32);
        Assert.Equal(1, sort[WorkItemSort.DeadlineField].AsInt32);
        Assert.Equal(-1, sort["submittedAt"].AsInt32);
        Assert.Equal(1, sort["_id"].AsInt32);
    }

    [Fact]
    public void BuildStages_due_date_descending_keeps_no_clock_items_last()
    {
        var stages = WorkItemSort.BuildStages("due-date", descendingOverride: true, s_rank);

        var (_, sort) = stages!.Value;
        // Direction flips on the deadline, but has-deadline stays -1 so
        // clock-less items never jump to the top under a descending sort.
        Assert.Equal(-1, sort[WorkItemSort.HasDeadlineField].AsInt32);
        Assert.Equal(-1, sort[WorkItemSort.DeadlineField].AsInt32);
    }
}
