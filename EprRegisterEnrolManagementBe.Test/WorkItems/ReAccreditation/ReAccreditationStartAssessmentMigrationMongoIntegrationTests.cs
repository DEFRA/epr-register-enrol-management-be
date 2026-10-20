using System.Security.Claims;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-595 ("add" step): runs the start-assessment rules migration against a
/// real ephemeral Mongo, on v13 and v14 fixtures in a spread of states
/// (including an assigned Duly made case). It must add
/// <c>start-assessment</c> and set v15, leave every other stored field exactly
/// as it was, write nothing on a second run, and leave migrated items able to
/// accept <c>start-assessment</c> through the engine.
/// </summary>
public sealed class ReAccreditationStartAssessmentMigrationMongoIntegrationTests
    : IAsyncDisposable
{
    private static readonly DateTime s_now = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

    private static readonly ClaimsPrincipal s_user = new(
        new ClaimsIdentity(
            [
                new Claim("cognito:client_id", "test-client"),
                new Claim("user:id", "alice-1"),
                new Claim("user:name", "Alice Example"),
            ],
            "test"
        )
    );

    private readonly TestMongoDbClientFactory _clientFactory;
    private readonly string _databaseName;
    private readonly WorkItemPersistence _persistence;

    public ReAccreditationStartAssessmentMigrationMongoIntegrationTests(
        MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("reaccred-start-assessment");
        _clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _persistence = new WorkItemPersistence(_clientFactory, NullLoggerFactory.Instance);
    }

    public async ValueTask DisposeAsync() =>
        await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);

    private static ReAccreditationStartAssessmentSnapshotMigration BuildMigration() =>
        new(NullLogger<ReAccreditationStartAssessmentSnapshotMigration>.Instance);

    /// <summary>
    /// v14: the live template without start-assessment, plus the
    /// payment-received transition v14 still declared (v16 removed it).
    /// </summary>
    private static WorkItemTemplateSnapshot BuildV14Snapshot()
    {
        var live = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        return new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v14",
            States = live.States,
            Transitions = live
                .Transitions.Where(t => t.ActionId != "start-assessment")
                .Append(new WorkItemTransition(
                    "payment-received", "Payment received", "duly-made", "assessment-in-progress"))
                .ToList(),
        };
    }

    /// <summary>v13: v14 with resume-during-duly-made still landing on 'updated'.</summary>
    private static WorkItemTemplateSnapshot BuildV13Snapshot()
    {
        var v14 = BuildV14Snapshot();
        return new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v13",
            States = v14.States,
            Transitions = v14
                .Transitions.Select(t =>
                    t.ActionId == "resume-during-duly-made" ? t with { ToStateId = "updated" } : t)
                .ToList(),
        };
    }

    private static WorkItemTemplateSnapshot SnapshotFor(string version) =>
        version == "v13" ? BuildV13Snapshot() : BuildV14Snapshot();

    private async Task<WorkItem> SeedAsync(
        string version,
        string stateId,
        string? assignedToId,
        CancellationToken ct)
    {
        var item = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = stateId,
            SubmittedAt = s_now.AddDays(-10),
            LastModifiedAt = s_now.AddDays(-2),
            SubmittedBy = "seed",
            AssignedToId = assignedToId,
            AssignedToName = assignedToId is null ? null : "Bob Colleague",
            AssignedAt = assignedToId is null ? null : s_now.AddDays(-3),
            AssignedBy = assignedToId,
            TemplateVersion = version,
            TemplateSnapshot = SnapshotFor(version),
            Payload = new BsonDocument
            {
                ["applicationReference"] = $"RA-595-{Guid.NewGuid():N}",
                ["operatorApplicationId"] = $"app-{Guid.NewGuid():N}",
                ["paymentDate"] = "2026-08-20",
            },
            SlaClock = new WorkItemSlaClock { StartedAt = new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc) },
        };
        item.Notes.Add(new WorkItemNote { Text = "Spoke to operator", CreatedAt = s_now.AddDays(-4), CreatedBy = "bob-1" });
        item.AuditLog.Add(new WorkItemAuditEntry
        {
            Action = "action-applied",
            ActionDisplayName = "Duly make",
            Details = new Dictionary<string, string?> { ["actionId"] = "duly-make" },
            CreatedAt = s_now.AddDays(-5),
            CreatedBy = "bob-1",
        });
        await _persistence.CreateAsync(item, ct);
        return item;
    }

    public static TheoryData<string, string, string?> Fixtures() =>
        new()
        {
            { "v13", "duly-made", "bob-1" },
            { "v14", "duly-made", "bob-1" },
            { "v13", "duly-made", null },
            { "v14", "duly-made", null },
            { "v14", "submitted", null },
            { "v13", "assessment-in-progress", "bob-1" },
            { "v14", "queried", "bob-1" },
            { "v14", "updated", null },
            { "v13", "approved", "bob-1" },
            { "v14", "withdrawn", null },
        };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Migration_adds_start_assessment_sets_v15_and_changes_nothing_else(
        string version,
        string stateId,
        string? assignedToId)
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedAsync(version, stateId, assignedToId, ct);
        var before = (await _persistence.GetByIdAsync(seeded.Id, ct))!;
        var transitionsBefore = before.TemplateSnapshot!.Transitions.ToList();

        await BuildMigration().ApplyAsync(_persistence, ct);

        var after = (await _persistence.GetByIdAsync(seeded.Id, ct))!;
        Assert.Equal("v15", after.TemplateVersion);
        Assert.Equal("v15", after.TemplateSnapshot!.TemplateVersion);
        var added = Assert.Single(after.TemplateSnapshot.Transitions, t => t.ActionId == "start-assessment");
        Assert.Equal("duly-made", added.FromStateId);
        Assert.Equal("assessment-in-progress", added.ToStateId);
        Assert.Equal("Assessment started", added.DisplayName);
        Assert.Equal(
            transitionsBefore,
            after.TemplateSnapshot.Transitions.Where(t => t.ActionId != "start-assessment").ToList());
        Assert.Equal(before.Version + 1, after.Version);

        // Field by field, the preserved things.
        Assert.Equal(stateId, after.StateId);
        Assert.Equal(assignedToId, after.AssignedToId);
        Assert.Equal(before.AssignedToName, after.AssignedToName);
        Assert.Equal(before.AssignedAt, after.AssignedAt);
        Assert.Equal("2026-08-20", after.Payload["paymentDate"].AsString);
        Assert.Equal(before.SlaClock!.StartedAt, after.SlaClock!.StartedAt);
        Assert.Equal(before.LastModifiedAt, after.LastModifiedAt);
        Assert.Single(after.Notes);
        Assert.Single(after.AuditLog);

        // Everything else: once the three fields the migration owns are put
        // back, the stored document is identical.
        after.TemplateSnapshot = before.TemplateSnapshot;
        after.TemplateVersion = before.TemplateVersion;
        after.Version = before.Version;
        Assert.Equal(before.ToBsonDocument(), after.ToBsonDocument());
    }

    [Fact]
    public async Task A_second_run_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var v13 = await SeedAsync("v13", "duly-made", "bob-1", ct);
        var v14 = await SeedAsync("v14", "assessment-in-progress", null, ct);

        await BuildMigration().ApplyAsync(_persistence, ct);
        var v13AfterFirst = (await _persistence.GetByIdAsync(v13.Id, ct))!;
        var v14AfterFirst = (await _persistence.GetByIdAsync(v14.Id, ct))!;

        await BuildMigration().ApplyAsync(_persistence, ct);
        var v13AfterSecond = (await _persistence.GetByIdAsync(v13.Id, ct))!;
        var v14AfterSecond = (await _persistence.GetByIdAsync(v14.Id, ct))!;

        Assert.Equal(v13AfterFirst.ToBsonDocument(), v13AfterSecond.ToBsonDocument());
        Assert.Equal(v14AfterFirst.ToBsonDocument(), v14AfterSecond.ToBsonDocument());
    }

    [Theory]
    [InlineData("v13", "bob-1")]
    [InlineData("v14", null)]
    public async Task A_migrated_duly_made_item_accepts_start_assessment(string version, string? assignedToId)
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedAsync(version, "duly-made", assignedToId, ct);
        var engine = new WorkItemService(
            new WorkItemRegistry([new ReAccreditationType()]),
            _persistence,
            NullLogger<WorkItemService>.Instance);

        // Positive control: before migrating, the frozen snapshot has no such action.
        Assert.False((await engine.ApplyActionAsync(seeded.Id, "start-assessment", s_user, ct)).IsSuccess);

        await BuildMigration().ApplyAsync(_persistence, ct);

        var result = await engine.ApplyActionAsync(seeded.Id, "start-assessment", s_user, ct);

        Assert.True(result.IsSuccess, result.Message);
        var stored = (await _persistence.GetByIdAsync(seeded.Id, ct))!;
        Assert.Equal("assessment-in-progress", stored.StateId);
        Assert.Equal(assignedToId, stored.AssignedToId);
        Assert.Contains(
            stored.AuditLog,
            e => e.Action == "action-applied"
                && e.Details.GetValueOrDefault("actionId") == "start-assessment"
                && e.Details.GetValueOrDefault("actionDisplayName") == "Assessment started");
    }
}
