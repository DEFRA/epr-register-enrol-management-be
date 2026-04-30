using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
        return services;
    }

    /// <summary>
    /// Conditionally register <see cref="WorkItemSeederHostedService"/>.
    /// The seeder writes records that reference stub user identifiers
    /// (e.g. <c>stub-standard-1</c>, <c>stub-assign-1</c>,
    /// <c>stub-portal-client</c>), so it must NEVER run outside a
    /// developer environment. Registration therefore requires both:
    /// <list type="bullet">
    ///   <item><see cref="IHostEnvironment.IsDevelopment"/> is <c>true</c></item>
    ///   <item><c>WorkItems:SeedOnStartup</c> configuration is <c>true</c></item>
    /// </list>
    /// If the flag is observed in any other environment a one-shot
    /// warning hosted service is registered so the misconfiguration is
    /// visible in startup logs without ever touching the database.
    /// </summary>
    public static IServiceCollection AddWorkItemSeederIfDevelopment(
        this IServiceCollection services,
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        var seedFlag = configuration.GetValue("WorkItems:SeedOnStartup", false);
        if (!seedFlag)
        {
            return services;
        }

        if (!environment.IsDevelopment())
        {
            services.AddHostedService<WorkItemSeederMisconfigurationWarningHostedService>();
            return services;
        }

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