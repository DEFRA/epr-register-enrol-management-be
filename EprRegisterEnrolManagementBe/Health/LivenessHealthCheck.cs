using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolManagementBe.Health;

/// <summary>
/// Configuration for <see cref="LivenessHealthCheck"/>. Exposed so the
/// timeout can be tightened in tests (or relaxed in production) via the
/// <c>Liveness:Timeout</c> configuration key without code changes.
/// </summary>
public sealed class LivenessHealthCheckOptions
{
    /// <summary>
    /// Maximum time the liveness probe will wait for a trivial task to be
    /// scheduled and complete on the thread pool before declaring the
    /// process unhealthy. Defaults to one second — generous enough to
    /// absorb a transient GC pause but tight enough that a wedged thread
    /// pool will be detected within a couple of probe intervals.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "00:01:00")]
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMilliseconds(1000);
}

/// <summary>
/// Liveness probe that exercises a real signal of process health beyond
/// "the request was parsed": it schedules a trivial work item on the
/// thread pool and waits for it to complete within
/// <see cref="LivenessHealthCheckOptions.Timeout"/>. If the work item
/// cannot be observed to complete in time the thread pool is wedged
/// (deadlock, exhaustion, GC death-spiral) and we report
/// <see cref="HealthStatus.Unhealthy"/> so Kubernetes / CDP recycles the
/// pod. Deliberately dependency-free — no I/O, no Mongo, no HTTP.
/// </summary>
public sealed class LivenessHealthCheck : IHealthCheck
{
    private readonly LivenessHealthCheckOptions _options;
    private readonly Func<CancellationToken, Task> _probe;

    public LivenessHealthCheck(IOptions<LivenessHealthCheckOptions> options)
        : this(options, static ct => Task.Run(static () => { }, ct))
    {
    }

    /// <summary>
    /// Test seam. Production wiring uses the public constructor which
    /// schedules a no-op on the thread pool; tests substitute a probe
    /// that never completes to assert the timeout path.
    /// </summary>
    internal LivenessHealthCheck(
        IOptions<LivenessHealthCheckOptions> options,
        Func<CancellationToken, Task> probe)
    {
        _options = options.Value;
        _probe = probe;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.Timeout);

        try
        {
            await _probe(cts.Token).WaitAsync(_options.Timeout, cancellationToken);
            return HealthCheckResult.Healthy("Thread pool responsive.");
        }
        catch (TimeoutException ex)
        {
            return HealthCheckResult.Unhealthy(
                "Thread pool did not respond within liveness timeout.", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy(
                "Thread pool did not respond within liveness timeout.", ex);
        }
    }
}
