using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// Unit tests for <see cref="WorkItemQuery"/>'s computed clamping
/// properties. These are pure and cheap to exercise directly, so no
/// mocking/fixture is needed.
/// </summary>
public class WorkItemQueryTests
{
    [Fact]
    public void NormalisedPage_clamps_a_page_below_one_up_to_one()
    {
        var query = new WorkItemQuery(Page: 0);

        Assert.Equal(1, query.NormalisedPage);
    }

    [Fact]
    public void NormalisedPage_clamps_a_page_above_the_cap_down_to_the_cap()
    {
        var query = new WorkItemQuery(Page: WorkItemQuery.MaxPage + 1);

        Assert.Equal(WorkItemQuery.MaxPage, query.NormalisedPage);
    }

    [Fact]
    public void NormalisedPage_passes_through_a_page_within_range()
    {
        var query = new WorkItemQuery(Page: 5);

        Assert.Equal(5, query.NormalisedPage);
    }

    [Fact]
    public void NormalisedPageSize_clamps_a_size_below_the_minimum_up_to_the_minimum()
    {
        var query = new WorkItemQuery(PageSize: WorkItemQuery.MinPageSize - 1);

        Assert.Equal(WorkItemQuery.MinPageSize, query.NormalisedPageSize);
    }

    [Fact]
    public void NormalisedPageSize_clamps_a_size_above_the_maximum_down_to_the_maximum()
    {
        var query = new WorkItemQuery(PageSize: WorkItemQuery.MaxPageSize + 1);

        Assert.Equal(WorkItemQuery.MaxPageSize, query.NormalisedPageSize);
    }

    [Fact]
    public void NormalisedPageSize_passes_through_a_size_within_range()
    {
        var query = new WorkItemQuery(PageSize: 50);

        Assert.Equal(50, query.NormalisedPageSize);
    }
}
