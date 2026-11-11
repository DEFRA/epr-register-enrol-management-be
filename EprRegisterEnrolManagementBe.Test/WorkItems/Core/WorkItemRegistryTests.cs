using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

public class WorkItemRegistryTests
{
    [Fact]
    public void Find_returns_registered_type_by_id()
    {
        var typeA = new TestWorkItemType("alpha", "Alpha");
        var typeB = new TestWorkItemType("beta", "Beta");

        var registry = new WorkItemRegistry([typeA, typeB]);

        Assert.Same(typeA, registry.Find("alpha"));
        Assert.Same(typeB, registry.Find("beta"));
    }

    [Fact]
    public void Find_is_case_insensitive_on_type_id()
    {
        var typeA = new TestWorkItemType("Alpha", "Alpha");

        var registry = new WorkItemRegistry([typeA]);

        Assert.Same(typeA, registry.Find("ALPHA"));
    }

    [Fact]
    public void Find_returns_null_when_type_is_not_registered()
    {
        var registry = new WorkItemRegistry([new TestWorkItemType("alpha", "Alpha")]);

        Assert.Null(registry.Find("missing"));
    }

    [Fact]
    public void Types_lists_every_registered_type()
    {
        var typeA = new TestWorkItemType("alpha", "Alpha");
        var typeB = new TestWorkItemType("beta", "Beta");

        var registry = new WorkItemRegistry([typeA, typeB]);

        Assert.Equal(2, registry.Types.Count);
        Assert.Contains(typeA, registry.Types);
        Assert.Contains(typeB, registry.Types);
    }

    [Fact]
    public void Constructor_throws_when_a_type_id_is_blank()
    {
        var invalid = new TestWorkItemType(" ", "Blank");

        Assert.Throws<InvalidWorkItemTypeException>(() => new WorkItemRegistry([invalid]));
    }

    [Fact]
    public void Constructor_throws_when_two_types_share_an_id()
    {
        var first = new TestWorkItemType("alpha", "First");
        var second = new TestWorkItemType("alpha", "Second");

        var ex = Assert.Throws<DuplicateWorkItemTypeException>(
            () => new WorkItemRegistry([first, second]));
        Assert.Equal("alpha", ex.TypeId);
    }

    // RA-739: the workflow is the single source of a type's state machine, so
    // a type whose engine-facing states disagree with its workflow is
    // rejected at startup rather than behaving inconsistently later.
    [Theory]
    [InlineData("initial")]
    [InlineData("states")]
    [InlineData("transitions")]
    public void Constructor_throws_when_a_type_disagrees_with_its_workflow(string mismatch)
    {
        var invalid = new MismatchedWorkflowType(mismatch);

        var ex = Assert.Throws<InvalidWorkItemTypeException>(() => new WorkItemRegistry([invalid]));
        Assert.Contains("mismatched", ex.Message);
    }

    [Fact]
    public void Constructor_accepts_a_type_that_takes_its_states_from_its_workflow()
    {
        var type = new MismatchedWorkflowType(mismatch: null);

        var registry = new WorkItemRegistry([type]);

        Assert.Same(type, registry.Find("mismatched"));
    }

    private sealed class MismatchedWorkflowType(string? mismatch) : IWorkItemType
    {
        private static readonly WorkItemState s_open = new("open", "Open");
        private static readonly WorkItemState s_closed = new("closed", "Closed", IsTerminal: true);
        private static readonly WorkItemTransition s_close = new("close", "Close", "open", "closed");
        private static readonly WorkItemWorkflow s_workflow = new("simple", s_open, [s_open, s_closed], [s_close]);

        public string TypeId => "mismatched";
        public string DisplayName => "Mismatched";
        public WorkItemWorkflow Workflow => s_workflow;
        public WorkItemState InitialState => mismatch == "initial" ? s_closed : Workflow.InitialState;
        public IReadOnlyCollection<WorkItemState> States => mismatch == "states" ? [s_open] : Workflow.States;
        public IReadOnlyCollection<WorkItemTransition> Transitions => mismatch == "transitions" ? [] : Workflow.Transitions;
    }
}