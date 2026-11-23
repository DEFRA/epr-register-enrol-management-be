using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Base for the re-accreditation <see cref="IWorkItemMigration"/> family: the
/// framework's <see cref="WorkItemMigrationBase"/> over every
/// re-accreditation work item. RA-495 review: the skeleton itself now lives in
/// the framework, so other types' migrations share it.
/// </summary>
internal abstract class ReAccreditationMigrationBase(ILogger logger)
    : WorkItemMigrationBase(ReAccreditationType.Id, logger);
