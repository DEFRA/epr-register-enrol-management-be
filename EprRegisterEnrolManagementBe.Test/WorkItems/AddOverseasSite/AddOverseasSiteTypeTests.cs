using EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.Workflows;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.AddOverseasSite;

public sealed class AddOverseasSiteTypeTests
{
    // RA-739: the add-ORS case type configuration.
    [Fact]
    public void Declares_its_case_type_configuration()
    {
        IWorkItemType type = new AddOverseasSiteType();

        Assert.Equal("add-overseas-site", type.TypeId);
        Assert.Equal("New ORS accreditation", type.DisplayName);
        Assert.Equal(WorkItemCategories.InYearChange, type.Category);
        Assert.Equal("ORS", type.InYearChangeCode);
        Assert.True(type.SplitsSubmissionPerSite);
        Assert.Same(StandardWorkflow.Definition, type.Workflow);
        Assert.Equal("submitted", type.InitialState.Id);
    }

    [Fact]
    public void Registers_alongside_re_accreditation_on_the_shared_standard_workflow()
    {
        var registry = new WorkItemRegistry(
            [new EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReAccreditationType(), new AddOverseasSiteType()]
        );

        Assert.NotNull(registry.Find("add-overseas-site"));
        Assert.NotNull(registry.Find("re-accreditation"));
    }
}
