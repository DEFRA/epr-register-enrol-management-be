using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// Edge-case coverage for the shared <c>ReAccreditationMigrationBase</c> /
/// <c>ReAccreditationSnapshotMigrationBase</c> template-method loop, exercised
/// through the real concrete migrations. These cover the two branches the
/// per-migration suites cannot reach because in those tests the query
/// candidate and the re-read document are the same object:
/// the <c>GetByIdAsync</c> re-read returning <c>null</c>, and the re-read
/// document already being current (the race the re-read guards against).
/// </summary>
public class ReAccreditationMigrationBaseTests
{
    private static WorkItemPage SinglePage(params WorkItem[] items) =>
        new(items, items.Length, 1, WorkItemQuery.MaxPageSize);

    [Fact]
    public async Task ApplyAsync_skips_when_reread_returns_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var candidate = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            Payload = new BsonDocument
            {
                ["materialsHandled"] = new BsonArray { "plastic" }
            }
        };

        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(candidate));
        persistence.GetByIdAsync(candidate.Id, ct).Returns((WorkItem?)null);

        var migration = new ReAccreditationMaterialBackfillMigration(
            NullLogger<ReAccreditationMaterialBackfillMigration>.Instance);

        await migration.ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
    }

    [Fact]
    public async Task ApplyAsync_skips_when_reread_document_is_already_current()
    {
        var ct = TestContext.Current.CancellationToken;
        var type = new ReAccreditationType();
        var currentSnapshot = WorkItemTemplateSnapshot.Capture(type);

        // Stale candidate the query still returns: a pre-migration v6 snapshot
        // with the resume-during-* transitions stripped, so ShouldConsider
        // passes and the base performs the re-read.
        var staleCandidate = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "queried",
            TemplateVersion = "v6",
            TemplateSnapshot = new WorkItemTemplateSnapshot
            {
                TemplateVersion = "v6",
                States = currentSnapshot.States,
                Transitions = currentSnapshot.Transitions
                    .Where(t => !t.ActionId.StartsWith("resume-during-", StringComparison.Ordinal))
                    .ToList()
            }
        };

        // The re-read returns a DIFFERENT, already-migrated document — as if a
        // concurrent instance migrated it between the query and the re-read.
        var freshFull = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "queried",
            TemplateVersion = "v7",
            TemplateSnapshot = currentSnapshot
        };

        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(staleCandidate));
        persistence.GetByIdAsync(staleCandidate.Id, ct).Returns(freshFull);

        var migration = new ReAccreditationResumeSnapshotMigration(
            NullLogger<ReAccreditationResumeSnapshotMigration>.Instance);

        await migration.ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
    }

    [Fact]
    public async Task ApplyAsync_skips_when_reread_document_is_already_current_for_duly_made_migration()
    {
        // Covers the same concurrent-migration race for the duly-made
        // migration, whose NeedsMigration additionally gates on a pre-v5
        // template version: the candidate still needs migrating but the re-read
        // document has already been migrated by another instance, so nothing is
        // saved.
        var ct = TestContext.Current.CancellationToken;
        var type = new ReAccreditationType();
        var currentSnapshot = WorkItemTemplateSnapshot.Capture(type);

        var staleCandidate = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            TemplateVersion = "v4",
            TemplateSnapshot = new WorkItemTemplateSnapshot
            {
                TemplateVersion = "v4",
                States = currentSnapshot.States,
                Transitions = currentSnapshot.Transitions
                    .Append(new WorkItemTransition("duly-make", "Mark as duly made", "submitted", "duly-made"))
                    .ToList()
            }
        };

        var freshFull = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            TemplateVersion = "v5",
            TemplateSnapshot = currentSnapshot
        };

        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(staleCandidate));
        persistence.GetByIdAsync(staleCandidate.Id, ct).Returns(freshFull);

        var migration = new ReAccreditationDulyMadeSnapshotMigration(
            NullLogger<ReAccreditationDulyMadeSnapshotMigration>.Instance);

        await migration.ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
    }
}
