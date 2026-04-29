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
}
