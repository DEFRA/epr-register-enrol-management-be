using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// Minimal in-test work item type used to exercise the framework. Real types
/// live in their own modules under <c>EprRegisterEnrolManagementBe/WorkItems/&lt;Type&gt;</c>.
/// </summary>
internal sealed class TestWorkItemType : IWorkItemType
{
    private readonly Dictionary<string, IReadOnlyCollection<WorkItemTask>> _tasksByState;

    public TestWorkItemType(
        string typeId,
        string displayName,
        WorkItemState? initialState = null,
        IReadOnlyCollection<WorkItemState>? states = null,
        Dictionary<string, IReadOnlyCollection<WorkItemTask>>? tasksByState = null,
        IReadOnlyCollection<WorkItemTransition>? transitions = null)
    {
        TypeId = typeId;
        DisplayName = displayName;
        InitialState = initialState ?? new WorkItemState("submitted", "Submitted");
        States = states ?? [InitialState];
        _tasksByState = tasksByState ?? new Dictionary<string, IReadOnlyCollection<WorkItemTask>>();
        Transitions = transitions ?? Array.Empty<WorkItemTransition>();
    }

    public string TypeId { get; }
    public string DisplayName { get; }
    public WorkItemState InitialState { get; }
    public IReadOnlyCollection<WorkItemState> States { get; }
    public IReadOnlyCollection<WorkItemTransition> Transitions { get; }

    public IReadOnlyCollection<WorkItemTask> GetTasksForState(string stateId) =>
        _tasksByState.TryGetValue(stateId, out var tasks) ? tasks : [];
}