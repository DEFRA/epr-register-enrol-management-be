namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Optional, module-owned hook for populating the work item collection with
/// example data on a fresh deployment. The framework runs every registered
/// seeder once at startup, but only when the persisted work item collection
/// is empty — so seed data never clobbers a real environment.
///
/// Modules opt in by registering an implementation of this interface from
/// their <see cref="IWorkItemModule.RegisterServices(IServiceCollection)"/>.
/// </summary>
public interface IWorkItemSeeder
{
    /// <summary>
    /// The <see cref="IWorkItemType.TypeId"/> the seeder belongs to. Used by
    /// the framework to look up the live type so the snapshot/version stamp
    /// on each seeded item matches what an in-flight item would carry.
    /// </summary>
    string TypeId { get; }

    /// <summary>
    /// Build the seed work items. Called from the seeding hosted service with
    /// the live <see cref="IWorkItemType"/> for <see cref="TypeId"/> and the
    /// application's <see cref="TimeProvider"/> so timestamps are consistent
    /// across seeders.
    /// </summary>
    IEnumerable<WorkItem> Build(IWorkItemType type, TimeProvider time);
}