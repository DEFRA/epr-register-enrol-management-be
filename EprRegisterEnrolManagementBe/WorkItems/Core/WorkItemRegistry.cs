namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Lookup over the work item types registered by modules. Resolved as a
/// singleton; safe to inject anywhere in the application.
/// </summary>
public interface IWorkItemRegistry
{
    IReadOnlyCollection<IWorkItemType> Types { get; }

    IWorkItemType? Find(string typeId);
}

public sealed class WorkItemRegistry : IWorkItemRegistry
{
    private readonly Dictionary<string, IWorkItemType> _types;

    public WorkItemRegistry(IEnumerable<IWorkItemType> types)
    {
        _types = new Dictionary<string, IWorkItemType>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in types)
        {
            if (string.IsNullOrWhiteSpace(type.TypeId))
            {
                throw new InvalidWorkItemTypeException("Work item type id cannot be empty.");
            }

            EnsureMatchesWorkflow(type);

            if (!_types.TryAdd(type.TypeId, type))
            {
                throw new DuplicateWorkItemTypeException(type.TypeId);
            }
        }
    }

    // RA-739: the engine reads a type's InitialState, States and Transitions;
    // its Workflow is the single place they are configured. Reject a type
    // whose two views disagree at startup rather than let it behave
    // inconsistently later.
    private static void EnsureMatchesWorkflow(IWorkItemType type)
    {
        var workflow = type.Workflow;
        if (
            type.InitialState != workflow.InitialState
            || !type.States.SequenceEqual(workflow.States)
            || !type.Transitions.SequenceEqual(workflow.Transitions)
        )
        {
            throw new InvalidWorkItemTypeException(
                $"Work item type '{type.TypeId}' must take its initial state, states and "
                    + $"transitions from its workflow '{workflow.Id}'."
            );
        }
    }

    public IReadOnlyCollection<IWorkItemType> Types => _types.Values.ToList().AsReadOnly();

    public IWorkItemType? Find(string typeId) =>
        typeId is not null && _types.TryGetValue(typeId, out var type) ? type : null;
}

public sealed class DuplicateWorkItemTypeException(string typeId)
    : Exception($"A work item type with id '{typeId}' is already registered.")
{
    public string TypeId { get; } = typeId;
}

public sealed class InvalidWorkItemTypeException(string message) : Exception(message);