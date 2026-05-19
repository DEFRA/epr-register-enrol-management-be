using EprRegisterEnrolManagementBe.Utils.Background;

namespace EprRegisterEnrolManagementBe.Test.Utils.Background;

public class BackgroundTaskQueueTests
{
    [Fact]
    public async Task QueueAsync_then_DequeueAsync_returns_same_delegate()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new BackgroundTaskQueue();
        Func<IServiceProvider, CancellationToken, Task> job = (_, _) => Task.CompletedTask;

        await sut.QueueAsync(job, ct);
        var dequeued = await sut.DequeueAsync(ct);

        Assert.Same(job, dequeued);
    }

    [Fact]
    public async Task Dequeues_in_FIFO_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new BackgroundTaskQueue();
        Func<IServiceProvider, CancellationToken, Task> a = (_, _) => Task.CompletedTask;
        Func<IServiceProvider, CancellationToken, Task> b = (_, _) => Task.CompletedTask;

        await sut.QueueAsync(a, ct);
        await sut.QueueAsync(b, ct);

        Assert.Same(a, await sut.DequeueAsync(ct));
        Assert.Same(b, await sut.DequeueAsync(ct));
    }

    [Fact]
    public async Task QueueAsync_throws_when_workItem_is_null()
    {
        var sut = new BackgroundTaskQueue();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => sut.QueueAsync(workItem: null!));
    }

    [Fact]
    public async Task DequeueAsync_throws_when_cancelled_before_item_arrives()
    {
        var sut = new BackgroundTaskQueue();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await sut.DequeueAsync(cts.Token));
    }
}
