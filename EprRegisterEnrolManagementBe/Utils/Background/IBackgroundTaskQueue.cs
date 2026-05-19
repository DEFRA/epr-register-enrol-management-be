namespace EprRegisterEnrolManagementBe.Utils.Background;

/// <summary>
/// RA-132: in-process queue of fire-and-forget background work. Used by
/// request-time code paths (e.g. the re-accreditation approval service) to
/// hand off side-effects whose failure must not bubble up to the caller.
///
/// Each queued job receives a freshly-scoped <see cref="IServiceProvider"/>
/// — created by <see cref="QueuedHostedService"/> — so jobs that resolve
/// scoped services (Mongo persistence, audit appender) are isolated from
/// the originating HTTP request scope.
/// </summary>
public interface IBackgroundTaskQueue
{
    /// <summary>
    /// Enqueue <paramref name="workItem"/> for processing on the hosted
    /// background worker. May block when the bounded queue is full so the
    /// caller back-pressures rather than silently dropping work.
    /// </summary>
    Task QueueAsync(
        Func<IServiceProvider, CancellationToken, Task> workItem,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Take the next queued job. Used by <see cref="QueuedHostedService"/>
    /// only; not part of the calling-side contract.
    /// </summary>
    ValueTask<Func<IServiceProvider, CancellationToken, Task>> DequeueAsync(
        CancellationToken cancellationToken);
}
