using System.Security.Claims;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-595 ("remove" step): runs the payment-received removal rules migration
/// against a real ephemeral Mongo, on v13, v14 and v15 fixtures in a spread of
/// states (including an assigned Duly made case). It must remove
/// <c>payment-received</c> and set v16, leave every other stored field exactly
/// as it was (including existing "Payment received" audit entries), write
/// nothing on a second run, and leave migrated items rejecting
/// <c>payment-received</c> as an unknown action while still accepting
/// <c>start-assessment</c>.
/// </summary>
public sealed class ReAccreditationRemovePaymentReceivedMigrationMongoIntegrationTests
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

    // The transition as v15 and earlier declared it, frozen as a literal: the
    // live type no longer has it.
    private static readonly WorkItemTransition s_paymentReceived = new(
        "payment-received",
        "Payment received",
        "duly-made",
        "assessment-in-progress"
    );

    private readonly TestMongoDbClientFactory _clientFactory;
    private readonly string _databaseName;
    private readonly WorkItemPersistence _persistence;

    public ReAccreditationRemovePaymentReceivedMigrationMongoIntegrationTests(
        MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("reaccred-remove-payment");
        _clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _persistence = new WorkItemPersistence(_clientFactory, NullLoggerFactory.Instance);
    }

    public async ValueTask DisposeAsync() =>
        await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);

    private static ReAccreditationRemovePaymentReceivedSnapshotMigration BuildMigration() =>
        new(NullLogger<ReAccreditationRemovePaymentReceivedSnapshotMigration>.Instance);

    /// <summary>v15: the live (v16) template plus payment-received.</summary>
    private static WorkItemTemplateSnapshot BuildV15Snapshot()
    {
        var live = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        var transitions = live.Transitions.ToList();
        var startAssessment = transitions.FindIndex(t => t.ActionId == "start-assessment");
        transitions.Insert(startAssessment, s_paymentReceived);
        return new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v15",
            States = live.States,
            Transitions = transitions,
        };
    }

    /// <summary>v14: v15 without start-assessment.</summary>
    private static WorkItemTemplateSnapshot BuildV14Snapshot()
    {
        var v15 = BuildV15Snapshot();
        return new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v14",
            States = v15.States,
            Transitions = v15.Transitions.Where(t => t.ActionId != "start-assessment").ToList(),
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
        version switch
        {
            "v13" => BuildV13Snapshot(),
            "v14" => BuildV14Snapshot(),
            _ => BuildV15Snapshot(),
        };

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
        if (stateId != "duly-made" && stateId != "submitted")
        {
            // A case that went through the old step keeps its history entry.
            item.AuditLog.Add(new WorkItemAuditEntry
            {
                Action = "action-applied",
                ActionDisplayName = "Payment received",
                Details = new Dictionary<string, string?>
                {
                    ["actionId"] = "payment-received",
                    ["actionDisplayName"] = "Payment received",
                },
                CreatedAt = s_now.AddDays(-4),
                CreatedBy = "bob-1",
            });
        }
        await _persistence.CreateAsync(item, ct);
        return item;
    }

    public static TheoryData<string, string, string?> Fixtures() =>
        new()
        {
            { "v13", "duly-made", "bob-1" },
            { "v14", "duly-made", "bob-1" },
            { "v15", "duly-made", "bob-1" },
            { "v13", "duly-made", null },
            { "v15", "duly-made", null },
            { "v15", "submitted", null },
            { "v13", "assessment-in-progress", "bob-1" },
            { "v15", "assessment-in-progress", "bob-1" },
            { "v14", "queried", "bob-1" },
            { "v15", "updated", null },
            { "v13", "approved", "bob-1" },
            { "v15", "withdrawn", null },
        };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Migration_removes_payment_received_sets_v16_and_changes_nothing_else(
        string version,
        string stateId,
        string? assignedToId)
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedAsync(version, stateId, assignedToId, ct);
        var before = (await _persistence.GetByIdAsync(seeded.Id, ct))!;

        await BuildMigration().ApplyAsync(_persistence, ct);

        var after = (await _persistence.GetByIdAsync(seeded.Id, ct))!;
        Assert.Equal("v16", after.TemplateVersion);
        Assert.Equal("v16", after.TemplateSnapshot!.TemplateVersion);
        Assert.DoesNotContain(after.TemplateSnapshot.Transitions, t => t.ActionId == "payment-received");
        var start = Assert.Single(after.TemplateSnapshot.Transitions, t => t.ActionId == "start-assessment");
        Assert.Equal("duly-made", start.FromStateId);
        Assert.Equal("assessment-in-progress", start.ToStateId);
        Assert.Equal("Assessment started", start.DisplayName);
        Assert.True(start.CallerInvocable);
        // Every other rule is untouched (v13 keeps its older resume target).
        Assert.Equal(
            before.TemplateSnapshot!.Transitions
                .Where(t => t.ActionId is not ("payment-received" or "start-assessment")).ToList(),
            after.TemplateSnapshot.Transitions
                .Where(t => t.ActionId != "start-assessment").ToList());
        Assert.Equal(before.TemplateSnapshot.States, after.TemplateSnapshot.States);
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
        Assert.Equal(before.AuditLog.Count, after.AuditLog.Count);

        // Everything else: once the three fields the migration owns are put
        // back, the stored document is identical.
        after.TemplateSnapshot = before.TemplateSnapshot;
        after.TemplateVersion = before.TemplateVersion;
        after.Version = before.Version;
        Assert.Equal(before.ToBsonDocument(), after.ToBsonDocument());
    }

    [Fact]
    public async Task Existing_payment_received_history_entries_are_kept_as_they_are()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedAsync("v15", "assessment-in-progress", "bob-1", ct);

        await BuildMigration().ApplyAsync(_persistence, ct);

        var after = (await _persistence.GetByIdAsync(seeded.Id, ct))!;
        var entry = Assert.Single(
            after.AuditLog,
            e => e.Details.GetValueOrDefault("actionId") == "payment-received");
        Assert.Equal("Payment received", entry.ActionDisplayName);
        Assert.Equal("Payment received", entry.Details.GetValueOrDefault("actionDisplayName"));
    }

    [Fact]
    public async Task A_second_run_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = new[]
        {
            await SeedAsync("v13", "duly-made", "bob-1", ct),
            await SeedAsync("v14", "queried", null, ct),
            await SeedAsync("v15", "assessment-in-progress", null, ct),
        };

        await BuildMigration().ApplyAsync(_persistence, ct);
        var afterFirst = new List<BsonDocument>();
        foreach (var item in seeded)
        {
            afterFirst.Add((await _persistence.GetByIdAsync(item.Id, ct))!.ToBsonDocument());
        }

        await BuildMigration().ApplyAsync(_persistence, ct);

        for (var i = 0; i < seeded.Length; i++)
        {
            var afterSecond = (await _persistence.GetByIdAsync(seeded[i].Id, ct))!;
            Assert.Equal(afterFirst[i], afterSecond.ToBsonDocument());
        }
    }

    [Fact]
    public async Task An_item_already_on_the_v16_rules_is_not_written()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "duly-made",
            SubmittedAt = s_now,
            LastModifiedAt = s_now,
            SubmittedBy = "seed",
            TemplateVersion = "v16",
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType()),
        };
        await _persistence.CreateAsync(item, ct);
        var before = (await _persistence.GetByIdAsync(item.Id, ct))!;

        await BuildMigration().ApplyAsync(_persistence, ct);

        var after = (await _persistence.GetByIdAsync(item.Id, ct))!;
        Assert.Equal(before.ToBsonDocument(), after.ToBsonDocument());
    }

    [Theory]
    [InlineData("v13", "bob-1")]
    [InlineData("v14", null)]
    [InlineData("v15", "bob-1")]
    [InlineData("v15", null)]
    public async Task A_migrated_duly_made_item_rejects_payment_received_and_accepts_start_assessment(
        string version,
        string? assignedToId)
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedAsync(version, "duly-made", assignedToId, ct);
        var engine = new WorkItemService(
            new WorkItemRegistry([new ReAccreditationType()]),
            _persistence,
            NullLogger<WorkItemService>.Instance);

        await BuildMigration().ApplyAsync(_persistence, ct);
        var migrated = (await _persistence.GetByIdAsync(seeded.Id, ct))!;

        var rejected = await engine.ApplyActionAsync(seeded.Id, "payment-received", s_user, ct);

        Assert.False(rejected.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.UnknownAction, rejected.FailureCode);
        var unchanged = (await _persistence.GetByIdAsync(seeded.Id, ct))!;
        Assert.Equal(migrated.ToBsonDocument(), unchanged.ToBsonDocument());

        var started = await engine.ApplyActionAsync(seeded.Id, "start-assessment", s_user, ct);

        Assert.True(started.IsSuccess, started.Message);
        var stored = (await _persistence.GetByIdAsync(seeded.Id, ct))!;
        Assert.Equal("assessment-in-progress", stored.StateId);
        Assert.Equal(assignedToId, stored.AssignedToId);
        Assert.Contains(
            stored.AuditLog,
            e => e.Action == "action-applied"
                && e.Details.GetValueOrDefault("actionId") == "start-assessment"
                && e.Details.GetValueOrDefault("actionDisplayName") == "Assessment started");
    }

    [Fact]
    public void It_is_registered_after_the_start_assessment_migration()
    {
        // RA-527: no longer the last snapshot migration — the v17
        // ReAccreditationPendingContactSnapshotMigration runs after it and
        // sets the final version (see its own integration tests).
        var services = new ServiceCollection();
        new ReAccreditationModule().RegisterServices(services, new ConfigurationBuilder().Build());

        var migrations = services
            .Where(d => d.ServiceType == typeof(IWorkItemMigration))
            .Select(d => d.ImplementationType)
            .ToList();

        Assert.True(
            migrations.IndexOf(typeof(ReAccreditationStartAssessmentSnapshotMigration))
            < migrations.IndexOf(typeof(ReAccreditationRemovePaymentReceivedSnapshotMigration)));
    }
}
