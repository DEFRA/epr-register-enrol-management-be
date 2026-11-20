using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Base for re-accreditation's "patch the frozen template snapshot"
/// migrations: the framework's <see cref="WorkItemSnapshotMigrationBase"/>
/// over every re-accreditation work item. RA-495 review: the skeleton itself
/// now lives in the framework, so other types' snapshot migrations share it.
/// </summary>
internal abstract class ReAccreditationSnapshotMigrationBase(ILogger logger)
    : WorkItemSnapshotMigrationBase(ReAccreditationType.Id, logger);
