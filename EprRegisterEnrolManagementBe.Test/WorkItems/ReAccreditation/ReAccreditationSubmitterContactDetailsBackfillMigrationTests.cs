using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

public class ReAccreditationSubmitterContactDetailsBackfillMigrationTests
{
    private static readonly Guid s_additionalInformationExporterId = WorkItemSeed.DeterministicId(
        ReAccreditationType.Id, ReAccreditationSeeder.AdditionalInformationExporterSeedKey);

    private static WorkItem BuildItem(BsonDocument? submitterContactDetails) => new()
    {
        Id = s_additionalInformationExporterId,
        TypeId = ReAccreditationType.Id,
        StateId = "submitted",
        Payload = submitterContactDetails is null
            ? new BsonDocument { ["organisationName"] = "Continental Exports Verification Ltd" }
            : new BsonDocument
            {
                ["organisationName"] = "Continental Exports Verification Ltd",
                ["submitterContactDetails"] = submitterContactDetails
            }
    };

    private static ReAccreditationSubmitterContactDetailsBackfillMigration BuildSut(
        TimeProvider? clock = null) =>
        new(NullLogger<ReAccreditationSubmitterContactDetailsBackfillMigration>.Instance, clock);

    private static IWorkItemPersistence BuildPersistence(WorkItem? item)
    {
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.GetByIdAsync(s_additionalInformationExporterId, Arg.Any<CancellationToken>())
            .Returns(item);
        return persistence;
    }

    [Fact]
    public async Task ApplyAsync_backfills_submitterContactDetails_onto_a_fixture_missing_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(null);
        var persistence = BuildPersistence(item);

        await BuildSut().ApplyAsync(persistence, ct);

        var submitterContactDetails = item.Payload["submitterContactDetails"].AsBsonDocument;
        Assert.Equal("Barton Deckow", submitterContactDetails["fullName"].AsString);
        Assert.Equal("REEXServiceTeam@defra.gov.uk", submitterContactDetails["email"].AsString);
        Assert.Equal("0111 478 4919", submitterContactDetails["phone"].AsString);
        Assert.Equal("Human Infrastructure Architect", submitterContactDetails["jobTitle"].AsString);
    }

    [Fact]
    public async Task ApplyAsync_appends_submitter_contact_details_backfilled_audit_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(null);
        var persistence = BuildPersistence(item);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Contains(item.AuditLog, e =>
            e.Action == "submitter-contact-details-backfilled" &&
            e.CreatedBy == "migration" &&
            e.Details["fullName"] == "Barton Deckow");
    }

    [Fact]
    public async Task ApplyAsync_skips_a_fixture_that_already_has_submitterContactDetails()
    {
        var ct = TestContext.Current.CancellationToken;
        var existing = new BsonDocument
        {
            ["fullName"] = "Already Backfilled",
            ["email"] = "already@example.com",
            ["phone"] = "0000 000 0000",
            ["jobTitle"] = "Existing Title",
        };
        var item = BuildItem(existing);
        var persistence = BuildPersistence(item);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
        Assert.Equal("Already Backfilled", existing["fullName"].AsString);
    }

    [Fact]
    public async Task ApplyAsync_skips_a_fixture_id_that_does_not_exist_yet()
    {
        var ct = TestContext.Current.CancellationToken;
        var persistence = BuildPersistence(null);

        // Must not throw when GetByIdAsync returns null.
        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    [Fact]
    public async Task ApplyAsync_uses_injected_time_for_audit_entry_created_at()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        var frozen = new DateTimeOffset(2026, 8, 15, 9, 0, 0, TimeSpan.Zero);
        clock.SetUtcNow(frozen);

        var item = BuildItem(null);
        var persistence = BuildPersistence(item);

        await BuildSut(clock).ApplyAsync(persistence, ct);

        var entry = item.AuditLog.Single(e => e.Action == "submitter-contact-details-backfilled");
        Assert.Equal(frozen.UtcDateTime, entry.CreatedAt);
    }

    [Fact]
    public async Task ApplyAsync_swallows_concurrency_exception()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(null);
        var persistence = BuildPersistence(item);
        persistence.ReplaceAsync(item, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(
                new WorkItemConcurrencyException(item.Id, expectedVersion: 0)));

        var exception = await Record.ExceptionAsync(() => BuildSut().ApplyAsync(persistence, ct));

        Assert.Null(exception);
    }
}
