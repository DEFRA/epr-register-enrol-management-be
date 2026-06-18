using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// Unit coverage for <see cref="TerminalStates.Ids"/>, the single source of
/// truth for which states are archivable / hidden by default (RA-224).
/// </summary>
public class TerminalStatesTests
{
    private static IWorkItemRegistry Registry(params IWorkItemType[] types) =>
        new WorkItemRegistry(types);

    [Fact]
    public void Ids_returns_only_terminal_states()
    {
        var registry = Registry(new TestWorkItemType(
            "t1", "T1",
            states: new[]
            {
                new WorkItemState("submitted", "Submitted"),
                new WorkItemState("approved", "Approved", IsTerminal: true),
                new WorkItemState("rejected", "Rejected", IsTerminal: true)
            }));

        var ids = TerminalStates.Ids(registry);

        Assert.Equal(new[] { "approved", "rejected" }.ToHashSet(), ids.ToHashSet());
    }

    [Fact]
    public void Ids_unions_and_dedupes_across_types()
    {
        var registry = Registry(
            new TestWorkItemType("t1", "T1", states: new[]
            {
                new WorkItemState("approved", "Approved", IsTerminal: true),
                new WorkItemState("withdrawn", "Withdrawn", IsTerminal: true)
            }),
            new TestWorkItemType("t2", "T2", states: new[]
            {
                new WorkItemState("approved", "Approved", IsTerminal: true),
                new WorkItemState("rejected", "Rejected", IsTerminal: true)
            }));

        var ids = TerminalStates.Ids(registry);

        Assert.Equal(
            new[] { "approved", "withdrawn", "rejected" }.ToHashSet(),
            ids.ToHashSet());
    }

    [Fact]
    public void Ids_is_case_insensitive()
    {
        var registry = Registry(new TestWorkItemType("t1", "T1", states: new[]
        {
            new WorkItemState("Approved", "Approved", IsTerminal: true)
        }));

        var ids = TerminalStates.Ids(registry);

        Assert.Contains("approved", ids);
        Assert.Contains("APPROVED", ids);
    }

    [Fact]
    public void Ids_returns_empty_when_no_terminal_states()
    {
        var registry = Registry(new TestWorkItemType("t1", "T1", states: new[]
        {
            new WorkItemState("submitted", "Submitted")
        }));

        Assert.Empty(TerminalStates.Ids(registry));
    }

    [Fact]
    public void Ids_throws_on_null_registry()
    {
        Assert.Throws<ArgumentNullException>(() => TerminalStates.Ids(null!));
    }
}
