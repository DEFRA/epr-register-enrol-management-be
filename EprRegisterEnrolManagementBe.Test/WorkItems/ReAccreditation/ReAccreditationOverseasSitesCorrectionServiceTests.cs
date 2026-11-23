using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-768 unit cover for <see cref="ReAccreditationOverseasSitesCorrectionService"/>:
/// the concurrency retry, matching edge cases and corrupt stored data. The
/// HTTP contract (statuses, 409 reasons, audit entry, 400s) is covered end to
/// end in <see cref="ReAccreditationEndpointTests"/>.
/// </summary>
public class ReAccreditationOverseasSitesCorrectionServiceTests
{
    private static readonly DateTimeOffset s_now = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    private static ClaimsPrincipal User(string? userId = "ra-768-overseas-sites-correction") =>
        new(
            new ClaimsIdentity(
                userId is null
                    ? [new Claim("cognito:client_id", "test-client")]
                    :
                    [
                        new Claim("cognito:client_id", "test-client"),
                        new Claim("user:id", userId),
                        new Claim("user:name", "RA-768 overseas sites correction"),
                    ],
                "test"
            )
        );

    private static WorkItem BuildWorkItem(BsonDocument payload) =>
        new()
        {
            Id = Guid.NewGuid(),
            TypeId = ReAccreditationType.Id,
            StateId = "queried",
            SubmittedAt = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc),
            LastModifiedAt = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc),
            SubmittedBy = "test-client",
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType()),
            TemplateVersion = new ReAccreditationType().TemplateVersion,
            Payload = payload,
        };

    private static BsonDocument WithSites(params BsonDocument[] sites) =>
        new() { ["overseasSites"] = new BsonDocument { ["sites"] = new BsonArray(sites) } };

    private static (ReAccreditationOverseasSitesCorrectionService Service, IWorkItemPersistence Persistence, List<WorkItem> Written) Build(
        WorkItem workItem,
        int conflictsBeforeSuccess = 0
    )
    {
        var persistence = Substitute.For<IWorkItemPersistence>();

        // Real persistence hands back a fresh document on every read, so a retry
        // never sees the previous attempt's in-memory edit.
        persistence
            .GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(_ => new WorkItem
            {
                Id = workItem.Id,
                TypeId = workItem.TypeId,
                StateId = workItem.StateId,
                SubmittedAt = workItem.SubmittedAt,
                LastModifiedAt = workItem.LastModifiedAt,
                SubmittedBy = workItem.SubmittedBy,
                TemplateSnapshot = workItem.TemplateSnapshot,
                TemplateVersion = workItem.TemplateVersion,
                Payload = workItem.Payload.DeepClone().AsBsonDocument,
            });

        var written = new List<WorkItem>();
        var conflicts = conflictsBeforeSuccess;
        persistence
            .ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (conflicts-- > 0)
                {
                    return Task.FromException(new WorkItemConcurrencyException(workItem.Id, 0));
                }
                written.Add(call.Arg<WorkItem>());
                return Task.CompletedTask;
            });

        var service = new ReAccreditationOverseasSitesCorrectionService(
            persistence,
            new WorkItemRegistry([new ReAccreditationType()]),
            new FakeTimeProvider(s_now),
            NullLogger<ReAccreditationOverseasSitesCorrectionService>.Instance
        );
        return (service, persistence, written);
    }

    [Fact]
    public async Task RemoveSitesAsync_retries_on_a_concurrency_conflict_and_writes_one_audit_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem(
            WithSites(
                new BsonDocument { ["siteId"] = 1, ["orsId"] = "ORS-A" },
                new BsonDocument { ["siteId"] = 2, ["orsId"] = "ORS-B" }
            )
        );
        var (service, persistence, written) = Build(workItem, conflictsBeforeSuccess: 1);

        var result = await service.RemoveSitesAsync(
            workItem.Id,
            [new OverseasSiteReference("ORS-B", null)],
            "RA-768: x",
            User(),
            ct
        );

        Assert.Null(result.Failure);
        Assert.Equal([new OverseasSiteReference("ORS-B", "2")], result.Removed);
        await persistence.Received(2).GetByIdAsync(workItem.Id, ct);
        await persistence.Received(2).ReplaceAsync(Arg.Any<WorkItem>(), ct);
        var saved = Assert.Single(written);
        Assert.Single(saved.AuditLog);
        Assert.Equal(s_now.UtcDateTime, saved.LastModifiedAt);
        Assert.Equal("queried", saved.StateId);
        Assert.Single(saved.Payload["overseasSites"]["sites"].AsBsonArray);
    }

    [Fact]
    public async Task RemoveSitesAsync_gives_up_with_a_concurrency_conflict_after_three_attempts()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem(WithSites(new BsonDocument { ["siteId"] = 1, ["orsId"] = "ORS-A" }));
        var (service, persistence, written) = Build(workItem, conflictsBeforeSuccess: 10);

        var result = await service.RemoveSitesAsync(
            workItem.Id,
            [new OverseasSiteReference("ORS-A", null)],
            "RA-768: x",
            User(),
            ct
        );

        Assert.Equal(WorkItemActionFailureCode.ConcurrencyConflict, result.Failure!.FailureCode);
        Assert.Equal(OverseasSitesRemovalResult.ConcurrencyConflictReason, result.ConflictReason);
        Assert.Empty(result.Removed);
        await persistence.Received(3).ReplaceAsync(Arg.Any<WorkItem>(), ct);
        Assert.Empty(written);
    }

    [Fact]
    public async Task RemoveSitesAsync_refuses_without_a_user_id_before_reading_the_work_item()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem(WithSites(new BsonDocument { ["siteId"] = 1, ["orsId"] = "ORS-A" }));
        var (service, persistence, _) = Build(workItem);

        var result = await service.RemoveSitesAsync(
            workItem.Id,
            [new OverseasSiteReference("ORS-A", null)],
            "RA-768: x",
            User(userId: null),
            ct
        );

        Assert.Equal(WorkItemActionFailureCode.MissingActorIdentity, result.Failure!.FailureCode);
        await persistence.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveSitesAsync_removes_every_stored_site_an_entry_matches_and_reports_a_repeat_entry_as_absent()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem(
            WithSites(
                new BsonDocument { ["siteId"] = 1, ["orsId"] = "ORS-A" },
                new BsonDocument { ["siteId"] = 2, ["orsId"] = "ORS-A" },
                new BsonDocument { ["siteId"] = 3, ["orsId"] = "ORS-C" }
            )
        );
        var (service, _, written) = Build(workItem);

        var result = await service.RemoveSitesAsync(
            workItem.Id,
            [new OverseasSiteReference("ORS-A", null), new OverseasSiteReference("ORS-A", null)],
            "RA-768: x",
            User(),
            ct
        );

        Assert.Equal(
            [new OverseasSiteReference("ORS-A", "1"), new OverseasSiteReference("ORS-A", "2")],
            result.Removed
        );
        Assert.Equal([new OverseasSiteReference("ORS-A", null)], result.AlreadyAbsent);
        var remaining = Assert.Single(Assert.Single(written).Payload["overseasSites"]["sites"].AsBsonArray);
        Assert.Equal("ORS-C", remaining["orsId"].AsString);
    }

    [Fact]
    public async Task RemoveSitesAsync_matches_a_string_siteId_and_keeps_entries_that_are_not_documents()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem(
            new BsonDocument
            {
                ["overseasSites"] = new BsonDocument
                {
                    ["sites"] = new BsonArray { new BsonDocument { ["siteId"] = "7" }, "not-a-site" },
                    ["otherKey"] = "must-survive",
                },
            }
        );
        var (service, _, written) = Build(workItem);

        var result = await service.RemoveSitesAsync(
            workItem.Id,
            [new OverseasSiteReference(null, "7")],
            "RA-768: x",
            User(),
            ct
        );

        Assert.Equal([new OverseasSiteReference(null, "7")], result.Removed);
        var overseasSites = Assert.Single(written).Payload["overseasSites"].AsBsonDocument;
        Assert.Equal(new BsonArray { "not-a-site" }, overseasSites["sites"]);
        Assert.Equal("must-survive", overseasSites["otherKey"].AsString);
    }

    [Theory]
    [MemberData(nameof(CorruptPayloads))]
    public async Task RemoveSitesAsync_throws_on_an_overseas_sites_field_with_the_wrong_shape(BsonDocument payload)
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem(payload);
        var (service, persistence, _) = Build(workItem);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RemoveSitesAsync(
                workItem.Id,
                [new OverseasSiteReference("ORS-A", null)],
                "RA-768: x",
                User(),
                ct
            )
        );
        await persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    public static TheoryData<BsonDocument> CorruptPayloads() =>
        new()
        {
            new BsonDocument { ["overseasSites"] = "not-a-document" },
            new BsonDocument { ["overseasSites"] = new BsonDocument { ["sites"] = "not-an-array" } },
        };

    [Theory]
    [MemberData(nameof(EmptyPayloads))]
    public async Task RemoveSitesAsync_reports_every_entry_absent_when_the_item_holds_no_sites(BsonDocument payload)
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem(payload);
        var (service, persistence, _) = Build(workItem);

        var result = await service.RemoveSitesAsync(
            workItem.Id,
            [new OverseasSiteReference("ORS-A", "1")],
            "RA-768: x",
            User(),
            ct
        );

        Assert.Null(result.Failure);
        Assert.Empty(result.Removed);
        Assert.Equal([new OverseasSiteReference("ORS-A", "1")], result.AlreadyAbsent);
        await persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    public static TheoryData<BsonDocument> EmptyPayloads() =>
        new()
        {
            new BsonDocument(),
            new BsonDocument { ["overseasSites"] = BsonNull.Value },
            new BsonDocument { ["overseasSites"] = new BsonDocument() },
            new BsonDocument { ["overseasSites"] = new BsonDocument { ["sites"] = new BsonArray() } },
        };
}
