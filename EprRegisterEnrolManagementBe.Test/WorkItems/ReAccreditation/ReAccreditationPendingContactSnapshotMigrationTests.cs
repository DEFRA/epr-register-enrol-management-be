using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-527 (v16 → v17). The Mongo round-trip on v16 fixtures (state and
/// transition added, every other field unchanged, second run writes nothing,
/// a migrated item can take a step 1 approval) is covered by
/// <see cref="ReAccreditationPendingContactMigrationMongoIntegrationTests"/>.
/// This covers the one case that can't be stored there: no snapshot at all.
/// </summary>
public class ReAccreditationPendingContactSnapshotMigrationTests
{
    [Fact]
    public async Task ApplyAsync_skips_an_item_with_no_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "assessment-in-progress",
            TemplateSnapshot = null,
            TemplateVersion = "v16",
            SubmittedAt = DateTime.UtcNow,
            LastModifiedAt = DateTime.UtcNow,
        };
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence
            .QueryAsync(Arg.Any<WorkItemQuery>(), ct)
            .Returns(new WorkItemPage([item], 1, 1, WorkItemQuery.MaxPageSize));

        await new ReAccreditationPendingContactSnapshotMigration(
            NullLogger<ReAccreditationPendingContactSnapshotMigration>.Instance
        ).ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
        Assert.Equal("v16", item.TemplateVersion);
    }
}
