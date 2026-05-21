using EprRegisterEnrolManagementBe.Utils.Background;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EprRegisterEnrolManagementBe.Test.Utils.Background;

public class QueuedHostedServiceTests
{
    private sealed class Marker { }

    private static ServiceProvider BuildRootProvider()
    {
        var services = new ServiceCollection();
        services.AddScoped<Marker>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Drains_queued_jobs_in_their_own_scope()
    {
        var ct = TestContext.Current.CancellationToken;
        var queue = new BackgroundTaskQueue();
        await using var root = BuildRootProvider();
        var hosted = new QueuedHostedService(
            queue, root.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<QueuedHostedService>.Instance);

        var observed = new TaskCompletionSource<Marker>();
        await queue.QueueAsync((sp, _) =>
        {
            observed.SetResult(sp.GetRequiredService<Marker>());
            return Task.CompletedTask;
        }, ct);

        using var stop = new CancellationTokenSource();
        await hosted.StartAsync(stop.Token);

        var marker = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.NotNull(marker);

        await stop.CancelAsync();
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Swallows_exceptions_thrown_by_a_job_and_continues_processing()
    {
        var ct = TestContext.Current.CancellationToken;
        var queue = new BackgroundTaskQueue();
        await using var root = BuildRootProvider();
        var hosted = new QueuedHostedService(
            queue, root.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<QueuedHostedService>.Instance);

        var secondRan = new TaskCompletionSource();
        await queue.QueueAsync((_, _) => throw new InvalidOperationException("boom"), ct);
        await queue.QueueAsync((_, _) => { secondRan.SetResult(); return Task.CompletedTask; }, ct);

        using var stop = new CancellationTokenSource();
        await hosted.StartAsync(stop.Token);

        await secondRan.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        await stop.CancelAsync();
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stops_when_host_stop_token_is_cancelled_while_queue_is_empty()
    {
        var queue = new BackgroundTaskQueue();
        await using var root = BuildRootProvider();
        var hosted = new QueuedHostedService(
            queue, root.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<QueuedHostedService>.Instance);

        using var stop = new CancellationTokenSource();
        await hosted.StartAsync(stop.Token);

        // Queue is empty → worker is parked on DequeueAsync.
        // Cancelling the stop token must unblock it cleanly.
        await stop.CancelAsync();
        await hosted.StopAsync(CancellationToken.None);
    }
}
