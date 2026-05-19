using System.Threading.Channels;

namespace EprRegisterEnrolManagementBe.Utils.Background;

/// <summary>
/// RA-132 bounded-channel <see cref="IBackgroundTaskQueue"/>. Capacity is
/// intentionally small (100) so a stuck consumer surfaces as back-pressure
/// on the calling endpoint rather than as unbounded memory growth.
/// <see cref="BoundedChannelFullMode.Wait"/> is paired with the single
/// reader to make ordering predictable.
/// </summary>
internal sealed class BackgroundTaskQueue : IBackgroundTaskQueue
{
    private const int QueueCapacity = 100;

    private readonly Channel<Func<IServiceProvider, CancellationToken, Task>> _queue =
        Channel.CreateBounded<Func<IServiceProvider, CancellationToken, Task>>(
            new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true
            });

    public async Task QueueAsync(
        Func<IServiceProvider, CancellationToken, Task> workItem,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        await _queue.Writer.WriteAsync(workItem, cancellationToken);
    }

    public ValueTask<Func<IServiceProvider, CancellationToken, Task>> DequeueAsync(
        CancellationToken cancellationToken) =>
        _queue.Reader.ReadAsync(cancellationToken);
}
