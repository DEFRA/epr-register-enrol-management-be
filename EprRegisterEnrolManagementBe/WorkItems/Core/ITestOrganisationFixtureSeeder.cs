using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// RA-000: module-owned end-to-end test fixtures. These are work items on
/// <c>e2e-</c> test organisations that a journey spec cannot create through
/// the API, because they need a shape or a history the API never produces
/// (a legacy template snapshot, an SLA clock started last year).
/// <see cref="TestOrganisationFixtureHostedService"/> resets every fixture
/// to the document built here on each startup wherever the journey suite
/// runs: Development demo seeding (compose) or <c>TestOrganisations__Enabled</c>
/// (CDP test).
///
/// Each document must carry a fixed <c>_id</c>, so a spec can open it
/// directly and a reset replaces the same item instead of adding another.
/// </summary>
public interface ITestOrganisationFixtureSeeder
{
    /// <summary>The <see cref="IWorkItemType.TypeId"/> the fixtures belong to.</summary>
    string TypeId { get; }

    /// <summary>
    /// Build the pristine fixture documents. <paramref name="type"/> is the
    /// live type, for fixtures that should carry the current template
    /// snapshot; <paramref name="time"/> anchors any dates.
    /// </summary>
    IEnumerable<BsonDocument> Build(IWorkItemType type, TimeProvider time);
}
