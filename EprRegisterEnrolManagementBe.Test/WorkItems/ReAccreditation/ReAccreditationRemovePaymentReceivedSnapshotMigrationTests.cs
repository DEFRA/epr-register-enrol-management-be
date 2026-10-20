using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-595 ("remove" step). The Mongo round-trip on v13–v15 fixtures (rules
/// updated, every other field unchanged, second run writes nothing, migrated
/// items reject payment-received and accept start-assessment) is covered by
/// <see cref="ReAccreditationRemovePaymentReceivedMigrationMongoIntegrationTests"/>.
/// This covers the one case that can't be stored there: no snapshot at all.
/// </summary>
public class ReAccreditationRemovePaymentReceivedSnapshotMigrationTests
{
    [Fact]
    public async Task ApplyAsync_skips_an_item_with_no_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "duly-made",
            TemplateSnapshot = null,
            TemplateVersion = "v15",
            SubmittedAt = DateTime.UtcNow,
            LastModifiedAt = DateTime.UtcNow,
        };
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence
            .QueryAsync(Arg.Any<WorkItemQuery>(), ct)
            .Returns(new WorkItemPage([item], 1, 1, WorkItemQuery.MaxPageSize));

        await new ReAccreditationRemovePaymentReceivedSnapshotMigration(
            NullLogger<ReAccreditationRemovePaymentReceivedSnapshotMigration>.Instance
        ).ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
        Assert.Equal("v15", item.TemplateVersion);
    }
}
