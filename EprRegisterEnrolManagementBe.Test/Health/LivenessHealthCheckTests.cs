using EprRegisterEnrolManagementBe.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolManagementBe.Test.Health;

public class LivenessHealthCheckTests
{
    [Fact]
    public async Task Reports_healthy_when_probe_completes_within_timeout()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = Options.Create(new LivenessHealthCheckOptions
        {
            Timeout = TimeSpan.FromSeconds(1)
        });
        var check = new LivenessHealthCheck(options, _ => Task.CompletedTask);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Reports_unhealthy_when_probe_does_not_complete_within_timeout()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = Options.Create(new LivenessHealthCheckOptions
        {
            Timeout = TimeSpan.FromMilliseconds(20)
        });
        // Probe that ignores cancellation entirely so WaitAsync must trip
        // the timeout — simulates a wedged thread pool that cannot pick
        // up scheduled work.
        var check = new LivenessHealthCheck(options, _ => new TaskCompletionSource().Task);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Reports_unhealthy_when_probe_is_cancelled_by_internal_timeout()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = Options.Create(new LivenessHealthCheckOptions
        {
            Timeout = TimeSpan.FromMilliseconds(20)
        });
        // Probe that respects the linked timeout token — exercises the
        // OperationCanceledException branch.
        var check = new LivenessHealthCheck(options, async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });

        var result = await check.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Default_constructor_uses_thread_pool_probe_and_returns_healthy()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = Options.Create(new LivenessHealthCheckOptions());
        var check = new LivenessHealthCheck(options);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }
}
