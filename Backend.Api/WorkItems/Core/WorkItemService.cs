using System.Security.Claims;
using Backend.Api.Utils.Auditing;

namespace Backend.Api.WorkItems.Core;

/// <summary>
/// Framework service object that drives task completion and state transitions
/// for every work item type. Lives in core because the rules ("you cannot
/// approve a work item with outstanding tasks", "you cannot invoke an action
/// that does not apply to the current state", etc.) are universal across
/// modules. Module-specific business logic belongs in module service objects
/// that may be called before/after this engine.
/// </summary>
public interface IWorkItemService
{
    Task<WorkItemActionResult> CompleteTaskAsync(
        Guid workItemId,
        string taskId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    Task<WorkItemActionResult> ApplyActionAsync(
        Guid workItemId,
        string actionId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compute the task progress and currently-available actions for a work
    /// item. Returns <c>null</c> when no work item exists with the supplied id.
    /// </summary>
    Task<WorkItemEngineProjection?> ProjectAsync(Guid workItemId, CancellationToken cancellationToken = default);

    /// <summary>Project an already-loaded work item. Pure; safe to call without I/O.</summary>
    WorkItemEngineProjection Project(WorkItem workItem);
}

/// <summary>Snapshot of a work item alongside its engine-derived view.</summary>
public sealed record WorkItemEngineProjection(
    WorkItem WorkItem,
    string TemplateVersion,
    IReadOnlyCollection<WorkItemTaskProgress> Tasks,
    IReadOnlyCollection<WorkItemTransition> AvailableActions);

public sealed class WorkItemService(
    IWorkItemRegistry registry,
    IWorkItemPersistence persistence,
    ILogger<WorkItemService> logger,
    TimeProvider? timeProvider = null) : IWorkItemService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<WorkItemActionResult> CompleteTaskAsync(
        Guid workItemId,
        string taskId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var (workItem, template, failure) = await LoadAsync(workItemId, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var tasks = template!.GetTasksForState(workItem!.StateId);
        var task = tasks.FirstOrDefault(t => string.Equals(t.Id, taskId, StringComparison.OrdinalIgnoreCase));
        if (task is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.TaskNotApplicable,
                $"Task '{taskId}' is not required for work item {workItemId} in state '{workItem.StateId}'.");
        }

        var bucket = GetCompletedBucket(workItem, workItem.StateId);
        if (bucket.Add(task.Id))
        {
            workItem.LastModifiedAt = _timeProvider.GetUtcNow().UtcDateTime;
            await persistence.ReplaceAsync(workItem, cancellationToken);
            logger.Audit(
                "Task {TaskId} marked complete on work item {WorkItemId} ({TypeId}) by {User}",
                task.Id, workItem.Id, workItem.TypeId, DescribeUser(user));
        }

        return WorkItemActionResult.Success(workItem);
    }

    public async Task<WorkItemActionResult> ApplyActionAsync(
        Guid workItemId,
        string actionId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var (workItem, template, failure) = await LoadAsync(workItemId, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var transition = template!.Transitions.FirstOrDefault(
            t => string.Equals(t.ActionId, actionId, StringComparison.OrdinalIgnoreCase));
        if (transition is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.UnknownAction,
                $"Action '{actionId}' is not declared by work item type '{workItem!.TypeId}'.");
        }

        var currentState = template.States.FirstOrDefault(
            s => string.Equals(s.Id, workItem!.StateId, StringComparison.OrdinalIgnoreCase));
        if (currentState?.IsTerminal == true)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.TerminalState,
                $"Work item {workItemId} is in terminal state '{currentState.Id}'; no actions are allowed.");
        }

        if (!string.Equals(transition.FromStateId, workItem!.StateId, StringComparison.OrdinalIgnoreCase))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Action '{actionId}' moves work items from '{transition.FromStateId}', " +
                $"but {workItemId} is in '{workItem.StateId}'.");
        }

        if (transition.RequiresAllTasksComplete && HasIncompleteTasks(template, workItem))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.IncompleteTasks,
                $"Action '{actionId}' requires every task for state '{workItem.StateId}' to be complete first.");
        }

        var previousState = workItem.StateId;
        workItem.StateId = transition.ToStateId;
        workItem.LastModifiedAt = _timeProvider.GetUtcNow().UtcDateTime;
        await persistence.ReplaceAsync(workItem, cancellationToken);
        logger.Audit(
            "Work item {WorkItemId} ({TypeId}) transitioned from {FromState} to {ToState} via action {ActionId} by {User}",
            workItem.Id, workItem.TypeId, previousState, workItem.StateId, transition.ActionId, DescribeUser(user));

        return WorkItemActionResult.Success(workItem);
    }

    public async Task<WorkItemEngineProjection?> ProjectAsync(Guid workItemId, CancellationToken cancellationToken = default)
    {
        var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);
        return workItem is null ? null : Project(workItem);
    }

    public WorkItemEngineProjection Project(WorkItem workItem)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        var template = ResolveTemplate(workItem);
        if (template is null)
        {
            // The work item exists but its module is no longer registered and
            // no snapshot is on file (e.g. a legacy item). Render it as having
            // no tasks and no available actions so callers can still display it.
            return new WorkItemEngineProjection(
                workItem,
                ResolveTemplateVersion(workItem),
                Array.Empty<WorkItemTaskProgress>(),
                Array.Empty<WorkItemTransition>());
        }

        var completed = workItem.CompletedTaskIdsByState.TryGetValue(workItem.StateId, out var done)
            ? done
            : new HashSet<string>();

        var taskProgress = template.GetTasksForState(workItem.StateId)
            .Select(task => new WorkItemTaskProgress(task.Id, task.DisplayName, completed.Contains(task.Id)))
            .ToList();

        var currentState = template.States.FirstOrDefault(
            s => string.Equals(s.Id, workItem.StateId, StringComparison.OrdinalIgnoreCase));
        var isTerminal = currentState?.IsTerminal == true;

        IReadOnlyCollection<WorkItemTransition> available = isTerminal
            ? Array.Empty<WorkItemTransition>()
            : template.Transitions
                .Where(t => string.Equals(t.FromStateId, workItem.StateId, StringComparison.OrdinalIgnoreCase))
                .Where(t => !t.RequiresAllTasksComplete || taskProgress.All(p => p.IsComplete))
                .ToList();

        return new WorkItemEngineProjection(workItem, template.TemplateVersion, taskProgress, available);
    }

    private async Task<(WorkItem? WorkItem, IWorkItemTemplate? Template, WorkItemActionResult? Failure)> LoadAsync(
        Guid workItemId, CancellationToken cancellationToken)
    {
        var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);
        if (workItem is null)
        {
            return (null, null, WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'."));
        }

        var template = ResolveTemplate(workItem);
        if (template is null)
        {
            return (workItem, null, WorkItemActionResult.Failure(
                WorkItemActionFailureCode.UnknownAction,
                $"Work item {workItemId} references unregistered type '{workItem.TypeId}' and has no stored template snapshot."));
        }

        return (workItem, template, null);
    }

    /// <summary>
    /// Pick the template the engine should reason about for a work item. The
    /// snapshot stored on the work item wins so that historical items keep
    /// their original task list and action set even if the live type has
    /// since changed; the live type is used only as a fallback for legacy
    /// items submitted before snapshots existed.
    /// </summary>
    private IWorkItemTemplate? ResolveTemplate(WorkItem workItem)
    {
        if (workItem.TemplateSnapshot is not null)
        {
            return workItem.TemplateSnapshot;
        }
        return registry.Find(workItem.TypeId);
    }

    private string ResolveTemplateVersion(WorkItem workItem) =>
        workItem.TemplateVersion
        ?? workItem.TemplateSnapshot?.TemplateVersion
        ?? registry.Find(workItem.TypeId)?.TemplateVersion
        ?? "unknown";

    private static HashSet<string> GetCompletedBucket(WorkItem workItem, string stateId)
    {
        if (!workItem.CompletedTaskIdsByState.TryGetValue(stateId, out var bucket))
        {
            bucket = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            workItem.CompletedTaskIdsByState[stateId] = bucket;
        }
        return bucket;
    }

    private static bool HasIncompleteTasks(IWorkItemTemplate template, WorkItem workItem)
    {
        var required = template.GetTasksForState(workItem.StateId);
        if (required.Count == 0)
        {
            return false;
        }
        var completed = workItem.CompletedTaskIdsByState.TryGetValue(workItem.StateId, out var done)
            ? done
            : (IReadOnlyCollection<string>)Array.Empty<string>();
        return required.Any(t => !completed.Contains(t.Id));
    }

    private static string DescribeUser(ClaimsPrincipal? user) =>
        user?.FindFirstValue("cognito:client_id")
        ?? user?.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? "unknown";
}
