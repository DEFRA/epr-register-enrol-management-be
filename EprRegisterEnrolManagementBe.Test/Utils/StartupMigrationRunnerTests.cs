using EprRegisterEnrolManagementBe.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EprRegisterEnrolManagementBe.Test.Utils;

/// <summary>
/// The startup-migration harness (epr-uf2): runs each registered migration once,
/// in order, in its own scope, and is best-effort — one migration throwing must
/// not stop the host coming up or prevent later migrations from running.
/// </summary>
public sealed class StartupMigrationRunnerTests
{
    private static ServiceProvider BuildProvider() =>
        new ServiceCollection().BuildServiceProvider();

    [Fact]
    public async Task RunAsync_runs_migrations_in_order_each_in_a_scope()
    {
        await using var provider = BuildProvider();
        var order = new List<string>();

        await StartupMigrationRunner.RunAsync(
            provider,
            NullLogger.Instance,
            [
                ("first", (services, _, _) =>
                {
                    Assert.NotSame(provider, services); // a fresh scope, not the root
                    order.Add("first");
                    return Task.CompletedTask;
                }),
                ("second", (_, _, _) =>
                {
                    order.Add("second");
                    return Task.CompletedTask;
                }),
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(["first", "second"], order);
    }

    [Fact]
    public async Task RunAsync_swallows_a_failure_and_continues_with_later_migrations()
    {
        await using var provider = BuildProvider();
        var ran = new List<string>();

        // The first migration throwing must not propagate (host startup must not
        // be blocked) and must not stop the second from running.
        var ex = await Record.ExceptionAsync(() => StartupMigrationRunner.RunAsync(
            provider,
            NullLogger.Instance,
            [
                ("boom", (_, _, _) => throw new InvalidOperationException("boom")),
                ("after", (_, _, _) => { ran.Add("after"); return Task.CompletedTask; }),
            ],
            TestContext.Current.CancellationToken));

        Assert.Null(ex);
        Assert.Equal(["after"], ran);
    }

    [Fact]
    public async Task RunAsync_is_a_noop_with_no_migrations()
    {
        await using var provider = BuildProvider();

        var ex = await Record.ExceptionAsync(() => StartupMigrationRunner.RunAsync(
            provider, NullLogger.Instance, [], TestContext.Current.CancellationToken));

        Assert.Null(ex);
    }
}
