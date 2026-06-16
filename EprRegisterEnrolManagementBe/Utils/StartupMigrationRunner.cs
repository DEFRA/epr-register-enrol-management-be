using Microsoft.Extensions.DependencyInjection;

namespace EprRegisterEnrolManagementBe.Utils;

/// <summary>
/// Harness for one-shot, best-effort data migrations that must run at startup
/// <em>before</em> the host begins serving — and, in particular, before
/// <see cref="Utils.Mongo.MongoService{T}"/> subclasses build their indexes in
/// their constructors.
///
/// <para>
/// CDP gives no way to run an ad-hoc migration against a deployed database, so
/// a correction that must happen once per environment is wired here and runs
/// inside the app on boot. Each migration:
/// <list type="bullet">
///   <item>runs in its own DI scope;</item>
///   <item>is <strong>best-effort</strong> — a failure is logged and startup
///   continues, so a transient error can never wedge the host (the invariant
///   the migration supports, e.g. a unique index, remains the hard guarantee
///   and surfaces any unresolved state loudly);</item>
///   <item>should be <strong>idempotent</strong>, so re-running it on every
///   boot is a no-op once it has been applied.</item>
/// </list>
/// </para>
///
/// <para>
/// A startup migration is temporary: once it has run in every environment,
/// delete it and leave this harness in place for the next one. See the
/// "Startup migrations" section of the README.
/// </para>
/// </summary>
public static class StartupMigrationRunner
{
    /// <summary>
    /// A single startup migration. Given a scoped service provider and a
    /// logger, perform its work. Implementations should be idempotent.
    /// </summary>
    public delegate Task StartupMigration(
        IServiceProvider services, ILogger logger, CancellationToken cancellationToken);

    /// <summary>
    /// Run each named migration in registration order, each in its own scope,
    /// logging and swallowing any failure so host startup is never blocked.
    /// </summary>
    public static async Task RunAsync(
        IServiceProvider services,
        ILogger logger,
        IReadOnlyList<(string Name, StartupMigration Migration)> migrations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(migrations);

        foreach (var (name, migration) in migrations)
        {
            try
            {
                logger.LogInformation("Running startup migration {Migration}.", name);
                await using var scope = services.CreateAsyncScope();
                await migration(scope.ServiceProvider, logger, cancellationToken);
                logger.LogInformation("Startup migration {Migration} complete.", name);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex, "Startup migration {Migration} failed; continuing startup.", name);
            }
        }
    }
}
