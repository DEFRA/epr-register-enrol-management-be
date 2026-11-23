using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

/// <summary>
/// RA-495: the case types that have opted in to the shared Standard-workflow
/// lifecycle services, looked up by type id.
/// </summary>
internal sealed class StandardLifecycleStrategies(IEnumerable<IStandardLifecycleStrategy> strategies)
    : StandardLifecycleStrategies<IStandardLifecycleStrategy>(strategies)
{
    /// <summary>A lookup holding only <paramref name="strategy"/>.</summary>
    public static StandardLifecycleStrategies Of(IStandardLifecycleStrategy strategy) => new([strategy]);
}

/// <summary>
/// RA-495: the opted-in case types, looked up by type id, as the
/// <typeparamref name="TStrategy"/> part of their strategies a service needs
/// (<see cref="StandardDulyMakingService"/> needs only the
/// <see cref="IStandardDulyMakingRules"/>).
/// </summary>
internal class StandardLifecycleStrategies<TStrategy>
    where TStrategy : IStandardDulyMakingRules
{
    private readonly IReadOnlyList<TStrategy> _strategies;

    public StandardLifecycleStrategies(IEnumerable<TStrategy> strategies)
    {
        _strategies = strategies.ToList();
        var duplicate = _strategies
            .GroupBy(s => s.Type.TypeId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Work item type '{duplicate.Key}' opted in to the Standard-workflow lifecycle more than once."
            );
        }
    }

    /// <summary>The strategy for <paramref name="typeId"/>, or null when that type has not opted in.</summary>
    public TStrategy? Find(string? typeId) =>
        _strategies.FirstOrDefault(s =>
            string.Equals(s.Type.TypeId, typeId, StringComparison.OrdinalIgnoreCase)
        );

    /// <summary>
    /// The failure for a work item whose type has not opted in. The endpoints
    /// map it to a 400 with <c>errorCode: wrong-work-item-type</c>. With a
    /// single opted-in type (a type's own routes) it names that type, as
    /// re-accreditation's routes always have.
    /// </summary>
    public WorkItemActionResult NotOptedIn(Guid workItemId, string typeId) =>
        WorkItemActionResult.Failure(
            WorkItemActionFailureCode.UnknownAction,
            _strategies.Count == 1
                ? $"Work item {workItemId} is of type '{typeId}', not '{_strategies[0].Type.TypeId}'."
                : $"Work item {workItemId} is of type '{typeId}', which does not use the shared "
                    + "Standard-workflow lifecycle."
        );
}
