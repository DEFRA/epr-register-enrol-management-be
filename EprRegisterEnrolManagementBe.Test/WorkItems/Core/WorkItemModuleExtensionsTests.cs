using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

public class WorkItemModuleExtensionsTests
{
    [Fact]
    public void AddWorkItemModule_registers_module_type_and_calls_register_services()
    {
        var services = new ServiceCollection();

        services.AddWorkItemFramework();
        services.AddWorkItemModule<RecordingModule>();

        var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IWorkItemRegistry>();
        Assert.Single(registry.Types);
        Assert.Equal("recording", registry.Find("recording")?.TypeId);

        // Module's RegisterServices should have run, registering the marker.
        Assert.NotNull(provider.GetService<RecordingModule.Marker>());
    }

    [Fact]
    public void AddWorkItemModule_exposes_module_via_IWorkItemModule()
    {
        var services = new ServiceCollection();
        services.AddWorkItemFramework();
        services.AddWorkItemModule<RecordingModule>();
        services.AddWorkItemModule<EndpointModule>();

        var provider = services.BuildServiceProvider();

        var modules = provider.GetServices<IWorkItemModule>().ToList();
        Assert.Equal(2, modules.Count);
        Assert.Contains(modules, m => m.Type.TypeId == "recording");
        Assert.Contains(modules, m => m.Type.TypeId == "endpoint");
    }

    [Fact]
    public void MapWorkItemModules_invokes_MapEndpoints_for_every_module()
    {
        var services = new ServiceCollection();
        services.AddRouting();
        services.AddWorkItemFramework();
        services.AddWorkItemModule<EndpointModule>();
        services.AddWorkItemModule<RecordingModule>();

        var provider = services.BuildServiceProvider();
        var endpoints = new TestEndpointRouteBuilder(provider);

        endpoints.MapWorkItemModules();

        var mapped = endpoints.DataSources
            .SelectMany(s => s.Endpoints)
            .Select(e => e.DisplayName)
            .ToList();

        Assert.Contains(mapped, name => name is not null && name.Contains("/work-items/endpoint/ping"));
    }

    private sealed class TestEndpointRouteBuilder(IServiceProvider services) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = services;
        public ICollection<EndpointDataSource> DataSources { get; } = [];
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    private sealed class RecordingModule : IWorkItemModule
    {
        public IWorkItemType Type { get; } = new TestWorkItemType("recording", "Recording");

        public void RegisterServices(IServiceCollection services) => services.AddSingleton<Marker>();

        public void MapEndpoints(IEndpointRouteBuilder endpoints)
        {
        }

        public sealed class Marker;
    }

    private sealed class EndpointModule : IWorkItemModule
    {
        public IWorkItemType Type { get; } = new TestWorkItemType("endpoint", "Endpoint");

        public void RegisterServices(IServiceCollection services)
        {
        }

        public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
            endpoints.MapGet("/work-items/endpoint/ping", () => "pong");
    }
}