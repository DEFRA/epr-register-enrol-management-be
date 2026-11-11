using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// Minimal in-test work item type used to exercise the framework. Real types
/// live in their own modules under <c>EprRegisterEnrolManagementBe/WorkItems/&lt;Type&gt;</c>.
/// </summary>
internal sealed class TestWorkItemType : IWorkItemType
{
    public TestWorkItemType(
        string typeId,
        string displayName,
        WorkItemState? initialState = null,
        IReadOnlyCollection<WorkItemState>? states = null,
        IReadOnlyCollection<WorkItemTransition>? transitions = null,
        string? inYearChangeCode = null,
        string? category = null)
    {
        TypeId = typeId;
        DisplayName = displayName;
        InitialState = initialState ?? new WorkItemState("submitted", "Submitted");
        States = states ?? [InitialState];
        Transitions = transitions ?? Array.Empty<WorkItemTransition>();
        InYearChangeCode = inYearChangeCode;
        Category = category;
    }

    public string TypeId { get; }
    public string DisplayName { get; }
    public WorkItemState InitialState { get; }
    public IReadOnlyCollection<WorkItemState> States { get; }
    public IReadOnlyCollection<WorkItemTransition> Transitions { get; }
    public string? InYearChangeCode { get; }
    public string? Category { get; }
}