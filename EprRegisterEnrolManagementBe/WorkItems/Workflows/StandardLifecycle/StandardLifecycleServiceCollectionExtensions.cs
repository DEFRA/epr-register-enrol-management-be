using System.Security.Claims;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Utils.Background;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.AspNetCore.HeaderPropagation;

namespace EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

/// <summary>
/// RA-495: DI wiring for the shared Standard-workflow lifecycle.
/// </summary>
internal static class StandardLifecycleServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared services behind
    /// <see cref="StandardLifecycleEndpoints"/>. Call once, alongside the work
    /// item framework. They serve every type that has opted in.
    /// </summary>
    public static IServiceCollection AddStandardLifecycleServices(this IServiceCollection services)
    {
        services.AddSingleton(sp => new StandardLifecycleStrategies(
            sp.GetServices<IStandardLifecycleStrategy>()
        ));
        services.AddSingleton<StandardDeterminationWrites>();
        services.AddSingleton(sp => new StandardDecisionPush(
            sp.GetRequiredService<IOperatorBackendPushAdapter>(),
            sp.GetRequiredService<IWorkItemAuditAppender>(),
            Logger<StandardDecisionPush>(sp)
        ));
        services.AddSingleton<IStandardDulyMakingService>(sp => new StandardDulyMakingService(
            sp.GetRequiredService<IWorkItemPersistence>(),
            sp.GetRequiredService<IWorkItemRegistry>(),
            sp.GetServices<IWorkItemPostActionHook>(),
            sp.GetRequiredService<TimeProvider>(),
            Logger<StandardDulyMakingService>(sp),
            new StandardLifecycleStrategies<IStandardDulyMakingRules>(sp.GetServices<IStandardLifecycleStrategy>())
        ));
        services.AddSingleton<IStandardLogDecisionService>(sp => new StandardLogDecisionService(
            sp.GetRequiredService<IWorkItemPersistence>(),
            sp.GetRequiredService<IWorkItemService>(),
            sp.GetRequiredService<IWorkItemRegistry>(),
            sp.GetRequiredService<StandardLifecycleStrategies>(),
            Logger<StandardLogDecisionService>(sp)
        ));
        services.AddSingleton<IStandardConfirmContactService>(sp => new StandardConfirmContactService(
            sp.GetRequiredService<IWorkItemPersistence>(),
            sp.GetRequiredService<IWorkItemRegistry>(),
            sp.GetRequiredService<StandardLifecycleStrategies>(),
            sp.GetRequiredService<StandardDecisionPush>(),
            Logger<StandardConfirmContactService>(sp),
            sp.GetRequiredService<TimeProvider>()
        ));
        return services;
    }

    /// <summary>
    /// Opts a type in to the shared lifecycle: registers its strategy and the
    /// post-commit status push hook for it (a no-op when the strategy has no
    /// push target). A module calls this from its
    /// <see cref="IWorkItemModule.RegisterServices"/>. Hooks run in
    /// registration order, so a module with an email hook registers it after
    /// this.
    /// </summary>
    public static IServiceCollection AddStandardLifecycle<TStrategy>(this IServiceCollection services)
        where TStrategy : class, IStandardLifecycleStrategy
    {
        services.AddSingleton<TStrategy>();
        services.AddSingleton<IStandardLifecycleStrategy>(sp => sp.GetRequiredService<TStrategy>());
        services.AddSingleton<IWorkItemPostActionHook, DeferredStatusPushHook<TStrategy>>();
        return services;
    }

    private static ILogger Logger<T>(IServiceProvider sp) =>
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<T>();

    /// <summary>
    /// The type's <see cref="StandardStatusPushHook"/>, built on first use.
    /// A strategy's determination writes take every post-action hook, this one
    /// included, so building it from the strategy up front would be a
    /// dependency cycle. RA-495 review: registered by type, not a factory, so a
    /// module's hook-order tests can find it among the registrations.
    /// </summary>
    internal sealed class DeferredStatusPushHook<TStrategy>(IServiceProvider services) : IWorkItemPostActionHook
        where TStrategy : class, IStandardLifecycleStrategy
    {
        private readonly Lazy<StandardStatusPushHook> _hook = new(() =>
        {
            var strategy = services.GetRequiredService<TStrategy>();
            return new StandardStatusPushHook(
                strategy.Type,
                strategy.OperatorJourneyPush,
                services.GetRequiredService<IWorkItemRegistry>(),
                services.GetRequiredService<IBackgroundTaskQueue>(),
                services.GetRequiredService<HeaderPropagationValues>(),
                Logger<StandardStatusPushHook>(services),
                strategy.PushesQueryResponseStatus
            );
        });

        public Task OnSubmittedAsync(
            WorkItem workItem,
            ClaimsPrincipal user,
            CancellationToken cancellationToken
        ) => _hook.Value.OnSubmittedAsync(workItem, user, cancellationToken);

        public Task OnActionAppliedAsync(
            WorkItem workItem,
            string actionId,
            string fromStateId,
            ClaimsPrincipal user,
            CancellationToken cancellationToken
        ) => _hook.Value.OnActionAppliedAsync(workItem, actionId, fromStateId, user, cancellationToken);

        public Task OnAssignmentChangedAsync(
            WorkItem workItem,
            WorkItemAssignmentChange change,
            ClaimsPrincipal user,
            CancellationToken cancellationToken
        ) => _hook.Value.OnAssignmentChangedAsync(workItem, change, user, cancellationToken);
    }
}
