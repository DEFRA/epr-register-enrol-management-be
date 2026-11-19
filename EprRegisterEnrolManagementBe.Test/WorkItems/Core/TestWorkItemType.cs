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
        string? category = null,
        bool splitsSubmissionPerSite = false)
    {
        TypeId = typeId;
        DisplayName = displayName;
        InitialState = initialState ?? new WorkItemState("submitted", "Submitted");
        States = states ?? [InitialState];
        Transitions = transitions ?? Array.Empty<WorkItemTransition>();
        InYearChangeCode = inYearChangeCode;
        Category = category;
        SplitsSubmissionPerSite = splitsSubmissionPerSite;
    }

    public string TypeId { get; }
    public string DisplayName { get; }
    public WorkItemState InitialState { get; }
    public IReadOnlyCollection<WorkItemState> States { get; }
    public IReadOnlyCollection<WorkItemTransition> Transitions { get; }
    public string? InYearChangeCode { get; }
    public string? Category { get; }

    /// <summary>
    /// RA-739: lets a test register a second type that submits one work item
    /// per site, which production does not have yet - <c>add-overseas-site</c>
    /// is the only one - so the cross-type paths can still be exercised.
    /// </summary>
    public bool SplitsSubmissionPerSite { get; }
}