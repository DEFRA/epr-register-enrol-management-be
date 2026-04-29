using Microsoft.AspNetCore.Routing;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// DI and routing wiring for the work item framework. Adding a new work item
/// type to the application means:
/// <list type="number">
///   <item>Create a folder under <c>WorkItems/&lt;TypeName&gt;</c></item>
///   <item>Implement <see cref="IWorkItemType"/> and <see cref="IWorkItemModule"/></item>
///   <item>Call <c>services.AddWorkItemModule&lt;YourModule&gt;()</c> in <c>Program.cs</c></item>
/// </list>
/// No changes are required to other modules or to core code.
/// </summary>
public static class WorkItemModuleExtensions
{
    /// <summary>
    /// Register the framework itself. Call once before adding modules.
    /// </summary>
    public static IServiceCollection AddWorkItemFramework(this IServiceCollection services)
    {
        services.AddSingleton<IWorkItemRegistry>(sp =>
            new WorkItemRegistry(sp.GetServices<IWorkItemType>()));
        services.AddSingleton<IWorkItemService, WorkItemService>();

        // Seeding is opt-in per module via IWorkItemSeeder; the hosted
        // service is always registered so seeded modules just work without
        // any additional wiring in Program.cs.
        services.AddHostedService<WorkItemSeederHostedService>();
        return services;
    }

    /// <summary>
    /// Register a single work item module: its type, its services, and itself
    /// as an <see cref="IWorkItemModule"/> so its endpoints can be mapped later.
    /// </summary>
    public static IServiceCollection AddWorkItemModule<TModule>(this IServiceCollection services)
        where TModule : class, IWorkItemModule, new()
    {
        var module = new TModule();
        services.AddSingleton<IWorkItemModule>(module);
        services.AddSingleton(module.Type);
        module.RegisterServices(services);
        return services;
    }

    /// <summary>
    /// Map every registered module's endpoints onto the application. Call once
    /// during endpoint configuration.
    /// </summary>
    public static IEndpointRouteBuilder MapWorkItemModules(this IEndpointRouteBuilder endpoints)
    {
        var modules = endpoints.ServiceProvider.GetServices<IWorkItemModule>();
        foreach (var module in modules)
        {
            module.MapEndpoints(endpoints);
        }
        return endpoints;
    }
}
