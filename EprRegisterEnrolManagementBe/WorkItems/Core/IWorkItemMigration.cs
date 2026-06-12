namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// A named, idempotent data migration that runs once at startup against
/// the work-item collection. Implementations are discovered via DI and
/// executed in registration order by <see cref="WorkItemMigrationHostedService"/>.
///
/// Migrations run in every environment so there is no need to apply them
/// manually after deployment. Each migration must be idempotent: running it
/// against an already-migrated database must be a no-op.
/// </summary>
public interface IWorkItemMigration
{
    /// <summary>Human-readable name logged at the start of each run.</summary>
    string Name { get; }

    /// <summary>
    /// Apply the migration. Must be idempotent — calling it more than once
    /// (e.g. on a rolling restart with multiple instances) must produce the
    /// same result as calling it once.
    /// </summary>
    Task ApplyAsync(IWorkItemPersistence persistence, CancellationToken cancellationToken);
}
