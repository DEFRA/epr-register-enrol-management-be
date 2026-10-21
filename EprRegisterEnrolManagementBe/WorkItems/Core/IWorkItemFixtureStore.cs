using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// RA-000: writes end-to-end test fixtures verbatim into the work-items
/// collection. Implemented by <see cref="WorkItemPersistence"/> so the
/// collection keeps a single writer, but kept off
/// <see cref="IWorkItemPersistence"/> because nothing in the engine or any
/// endpoint may write an unvalidated document. Its only caller is
/// <see cref="TestOrganisationFixtureHostedService"/>, which never runs in
/// prod (see <see cref="WorkItemModuleExtensions.AddWorkItemSeeders"/>).
/// </summary>
public interface IWorkItemFixtureStore
{
    /// <summary>
    /// Replace the document whose <c>_id</c> matches
    /// <paramref name="document"/>'s, inserting it when absent. The
    /// document is written as-is, with no round trip through
    /// <see cref="WorkItem"/>, so it may carry elements the model no longer
    /// declares (a legacy template snapshot, for example). Whatever the
    /// stored item had become — state, assignment, audit log, version — is
    /// discarded.
    /// </summary>
    Task ResetAsync(BsonDocument document, CancellationToken cancellationToken = default);
}
