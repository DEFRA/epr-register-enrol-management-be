using System.Security.Claims;
using EprRegisterEnrolManagementBe.Config;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.Utils.Background;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-527 (v16 → v17): runs the pending-contact rules migration against a real
/// ephemeral Mongo, on v16 fixtures in a spread of states. It must add the
/// non-terminal <c>approved-pending-contact</c> and <c>rejected-pending-contact</c>
/// states, each outcome's step 1 (<c>make-determination-*</c>) and step 2
/// (<c>confirm-contact-*</c>) transitions, remove the single-step
/// <c>reject</c>, set v17, leave every other stored field exactly as it was,
/// write nothing on a second run, and leave a migrated decision-ready item
/// able to take a step 1 determination (which an unmigrated v16 item cannot).
/// </summary>
public sealed class ReAccreditationPendingContactMigrationMongoIntegrationTests
    : IAsyncDisposable
{
    private static readonly DateTime s_now = new(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);

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

    public ReAccreditationPendingContactMigrationMongoIntegrationTests(
        MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("reaccred-pending-contact");
        _clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _persistence = new WorkItemPersistence(_clientFactory, NullLoggerFactory.Instance);
    }

    public async ValueTask DisposeAsync() =>
        await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);

    private static ReAccreditationPendingContactSnapshotMigration BuildMigration() =>
        new(NullLogger<ReAccreditationPendingContactSnapshotMigration>.Instance);

    private static readonly string[] s_v17ActionIds =
    [
        "make-determination-approved",
        "confirm-contact-approved",
        "make-determination-rejected",
        "confirm-contact-rejected",
    ];

    private static readonly string[] s_v17StateIds =
        ["approved-pending-contact", "rejected-pending-contact"];

    // The single-step refusal v16 declared and v17 removes, as v12 stored it.
    private static readonly WorkItemTransition s_reject = new(
        "reject",
        "Reject",
        "awaiting-decision",
        "rejected",
        CallerInvocable: false
    );

    /// <summary>
    /// v16: the live (v17) template without the pending-contact states and
    /// transitions, and with the single-step reject.
    /// </summary>
    private static WorkItemTemplateSnapshot BuildV16Snapshot()
    {
        var live = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        return new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v16",
            States = live.States.Where(s => !s_v17StateIds.Contains(s.Id)).ToList(),
            Transitions = live
                .Transitions.Where(t => !s_v17ActionIds.Contains(t.ActionId))
                .Append(s_reject)
                .ToList(),
        };
    }

    private async Task<WorkItem> SeedAsync(
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
            TemplateVersion = "v16",
            TemplateSnapshot = BuildV16Snapshot(),
            Payload = new BsonDocument
            {
                ["applicationReference"] = $"RA-527-{Guid.NewGuid():N}",
                ["operatorOrganisationId"] = "500027",
                ["operatorApplicationId"] = $"app-{Guid.NewGuid():N}",
                ["nation"] = "England",
                ["paymentDate"] = "2026-08-20",
            },
            SlaClock = new WorkItemSlaClock
            {
                StartedAt = new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc),
            },
        };
        item.Notes.Add(new WorkItemNote { Text = "Spoke to operator", CreatedAt = s_now.AddDays(-4), CreatedBy = "bob-1" });
        item.AuditLog.Add(new WorkItemAuditEntry
        {
            Action = "action-applied",
            ActionDisplayName = "Action applied",
            Details = new Dictionary<string, string?> { ["actionId"] = "duly-make" },
            CreatedAt = s_now.AddDays(-5),
            CreatedBy = "bob-1",
        });
        await _persistence.CreateAsync(item, ct);
        return item;
    }

    public static TheoryData<string, string?> Fixtures() =>
        new()
        {
            { "submitted", null },
            { "duly-made", "bob-1" },
            { "assessment-in-progress", "bob-1" },
            { "assessment-in-progress", null },
            { "awaiting-decision", "bob-1" },
            { "queried", "bob-1" },
            { "updated", null },
            { "approved", "bob-1" },
            { "rejected", null },
            { "withdrawn", null },
        };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Migration_adds_the_pending_contact_rules_removes_reject_sets_v17_and_changes_nothing_else(
        string stateId,
        string? assignedToId)
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedAsync(stateId, assignedToId, ct);
        var before = (await _persistence.GetByIdAsync(seeded.Id, ct))!;

        await BuildMigration().ApplyAsync(_persistence, ct);

        var after = (await _persistence.GetByIdAsync(seeded.Id, ct))!;
        Assert.Equal("v17", after.TemplateVersion);
        Assert.Equal("v17", after.TemplateSnapshot!.TemplateVersion);

        var pending = Assert.Single(after.TemplateSnapshot.States, s => s.Id == "approved-pending-contact");
        Assert.Equal("Approved", pending.DisplayName);
        Assert.False(pending.IsTerminal);

        var step1 = Assert.Single(
            after.TemplateSnapshot.Transitions, t => t.ActionId == "make-determination-approved");
        Assert.Equal("awaiting-decision", step1.FromStateId);
        Assert.Equal("approved-pending-contact", step1.ToStateId);
        Assert.False(step1.CallerInvocable);

        var step2 = Assert.Single(
            after.TemplateSnapshot.Transitions, t => t.ActionId == "confirm-contact-approved");
        Assert.Equal("approved-pending-contact", step2.FromStateId);
        Assert.Equal("approved", step2.ToStateId);
        Assert.False(step2.CallerInvocable);

        var refusedPending = Assert.Single(after.TemplateSnapshot.States, s => s.Id == "rejected-pending-contact");
        Assert.Equal("Refused", refusedPending.DisplayName);
        Assert.False(refusedPending.IsTerminal);

        var refusalStep1 = Assert.Single(
            after.TemplateSnapshot.Transitions, t => t.ActionId == "make-determination-rejected");
        Assert.Equal("awaiting-decision", refusalStep1.FromStateId);
        Assert.Equal("rejected-pending-contact", refusalStep1.ToStateId);
        Assert.False(refusalStep1.CallerInvocable);

        var refusalStep2 = Assert.Single(
            after.TemplateSnapshot.Transitions, t => t.ActionId == "confirm-contact-rejected");
        Assert.Equal("rejected-pending-contact", refusalStep2.FromStateId);
        Assert.Equal("rejected", refusalStep2.ToStateId);
        Assert.False(refusalStep2.CallerInvocable);

        Assert.DoesNotContain(after.TemplateSnapshot.Transitions, t => t.ActionId == "reject");

        // The migrated rules are exactly the live v17 rules.
        var live = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        Assert.Equal(
            live.States.OrderBy(s => s.Id).ToList(),
            after.TemplateSnapshot.States.OrderBy(s => s.Id).ToList());
        Assert.Equal(
            live.Transitions.OrderBy(t => t.ActionId).ThenBy(t => t.FromStateId).ToList(),
            after.TemplateSnapshot.Transitions.OrderBy(t => t.ActionId).ThenBy(t => t.FromStateId).ToList());

        // Every other rule is untouched.
        Assert.Equal(
            before.TemplateSnapshot!.Transitions.Where(t => t.ActionId != "reject").ToList(),
            after.TemplateSnapshot.Transitions.Where(t => !s_v17ActionIds.Contains(t.ActionId)).ToList());
        Assert.Equal(
            before.TemplateSnapshot.States,
            after.TemplateSnapshot.States.Where(s => !s_v17StateIds.Contains(s.Id)).ToList());
        Assert.Equal(before.Version + 1, after.Version);

        // Field by field, the preserved things.
        Assert.Equal(stateId, after.StateId);
        Assert.Equal(assignedToId, after.AssignedToId);
        Assert.Equal(before.LastModifiedAt, after.LastModifiedAt);
        Assert.Equal(before.SlaClock!.StartedAt, after.SlaClock!.StartedAt);
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
    public async Task A_second_run_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = new[]
        {
            await SeedAsync("assessment-in-progress", "bob-1", ct),
            await SeedAsync("queried", null, ct),
            await SeedAsync("approved", null, ct),
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
    public async Task An_item_already_on_the_v17_rules_is_not_written()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "assessment-in-progress",
            SubmittedAt = s_now,
            LastModifiedAt = s_now,
            SubmittedBy = "seed",
            TemplateVersion = "v17",
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType()),
        };
        await _persistence.CreateAsync(item, ct);
        var before = (await _persistence.GetByIdAsync(item.Id, ct))!;

        await BuildMigration().ApplyAsync(_persistence, ct);

        var after = (await _persistence.GetByIdAsync(item.Id, ct))!;
        Assert.Equal(before.ToBsonDocument(), after.ToBsonDocument());
    }

    /// <summary>
    /// RA-527: v17 was extended in place while unreleased. An item already
    /// stamped v17 by an earlier build of this migration — task 003's (the
    /// approval state and step 1 only) or task 004's (approvals only), both
    /// still carrying reject — gets exactly the live v17 rules: what it lacks
    /// is added and reject is removed.
    /// </summary>
    [Theory]
    [InlineData("approved-pending-contact", "task-003")]
    [InlineData("approved-pending-contact", "task-004")]
    [InlineData("awaiting-decision", "task-004")]
    public async Task An_item_on_earlier_v17_rules_gains_exactly_the_live_v17_rules(
        string stateId,
        string build)
    {
        var ct = TestContext.Current.CancellationToken;
        var live = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        string[] missingActionIds = build == "task-003"
            ?
            [
                "confirm-contact-approved",
                "make-determination-rejected",
                "confirm-contact-rejected",
            ]
            : ["make-determination-rejected", "confirm-contact-rejected"];
        var item = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = stateId,
            SubmittedAt = s_now,
            LastModifiedAt = s_now,
            SubmittedBy = "seed",
            TemplateVersion = "v17",
            TemplateSnapshot = new WorkItemTemplateSnapshot
            {
                TemplateVersion = "v17",
                States = live.States.Where(s => s.Id != "rejected-pending-contact").ToList(),
                Transitions = live
                    .Transitions.Where(t => !missingActionIds.Contains(t.ActionId))
                    .Append(s_reject)
                    .ToList(),
            },
        };
        await _persistence.CreateAsync(item, ct);

        await BuildMigration().ApplyAsync(_persistence, ct);

        var after = (await _persistence.GetByIdAsync(item.Id, ct))!;
        Assert.Equal(stateId, after.StateId);
        Assert.Equal(
            live.Transitions.OrderBy(t => t.ActionId).ThenBy(t => t.FromStateId).ToList(),
            after.TemplateSnapshot!.Transitions.OrderBy(t => t.ActionId).ThenBy(t => t.FromStateId).ToList());
        Assert.Equal(
            live.States.OrderBy(s => s.Id).ToList(),
            after.TemplateSnapshot.States.OrderBy(s => s.Id).ToList());
    }

    /// <summary>
    /// RA-527 (task 010): an earlier build of v17 labelled the step 1 refusal
    /// state "Refusing". The label is now "Refused" (still v17, which is
    /// unreleased), so an item already stamped v17 with the old label has just
    /// that label corrected: its state, every other rule and every other
    /// stored field stay as they were, and a second run writes nothing.
    /// </summary>
    [Theory]
    [InlineData("rejected-pending-contact")]
    [InlineData("awaiting-decision")]
    [InlineData("rejected")]
    public async Task An_item_on_v17_with_the_earlier_refusal_label_has_only_that_label_corrected(
        string stateId)
    {
        var ct = TestContext.Current.CancellationToken;
        var live = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        var earlierStates = live
            .States.Select(s =>
                s.Id == "rejected-pending-contact" ? s with { DisplayName = "Refusing" } : s)
            .ToList();
        var item = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = stateId,
            SubmittedAt = s_now,
            LastModifiedAt = s_now,
            SubmittedBy = "seed",
            TemplateVersion = "v17",
            TemplateSnapshot = new WorkItemTemplateSnapshot
            {
                TemplateVersion = "v17",
                States = earlierStates,
                Transitions = live.Transitions.ToList(),
            },
        };
        await _persistence.CreateAsync(item, ct);
        var before = (await _persistence.GetByIdAsync(item.Id, ct))!;

        await BuildMigration().ApplyAsync(_persistence, ct);

        var after = (await _persistence.GetByIdAsync(item.Id, ct))!;
        Assert.Equal(
            "Refused",
            Assert.Single(after.TemplateSnapshot!.States, s => s.Id == "rejected-pending-contact").DisplayName);
        // Exactly the live v17 rules, in the order they were stored.
        Assert.Equal(live.States, after.TemplateSnapshot.States);
        Assert.Equal(live.Transitions, after.TemplateSnapshot.Transitions);
        Assert.Equal("v17", after.TemplateVersion);
        Assert.Equal(before.Version + 1, after.Version);

        // Nothing else moved.
        after.TemplateSnapshot = before.TemplateSnapshot;
        after.Version = before.Version;
        Assert.Equal(before.ToBsonDocument(), after.ToBsonDocument());

        // And a second run leaves the corrected item alone.
        var afterFirst = (await _persistence.GetByIdAsync(item.Id, ct))!.ToBsonDocument();
        await BuildMigration().ApplyAsync(_persistence, ct);
        Assert.Equal(afterFirst, (await _persistence.GetByIdAsync(item.Id, ct))!.ToBsonDocument());
    }

    [Fact]
    public async Task A_migrated_item_can_take_a_step_one_refusal_and_an_unmigrated_one_cannot()
    {
        var ct = TestContext.Current.CancellationToken;
        var migrated = await SeedAsync("awaiting-decision", "bob-1", ct);
        await BuildMigration().ApplyAsync(_persistence, ct);
        var unmigrated = await SeedAsync("awaiting-decision", "bob-1", ct);
        var service = new ReAccreditationRefusalService(
            _persistence,
            new WorkItemRegistry([new ReAccreditationType()]),
            [],
            NullLogger<ReAccreditationRefusalService>.Instance
        );

        var refused = await service.RecordRefusedDeterminationAsync(unmigrated.Id, s_user, ct);
        var recorded = await service.RecordRefusedDeterminationAsync(migrated.Id, s_user, ct);

        Assert.False(refused.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, refused.FailureCode);
        Assert.Equal("awaiting-decision", (await _persistence.GetByIdAsync(unmigrated.Id, ct))!.StateId);

        Assert.True(recorded.IsSuccess, recorded.Message);
        var stored = (await _persistence.GetByIdAsync(migrated.Id, ct))!;
        Assert.Equal("rejected-pending-contact", stored.StateId);
        Assert.Equal("bob-1", stored.AssignedToId);
    }

    [Fact]
    public async Task A_migrated_item_can_take_a_step_one_approval_and_an_unmigrated_one_cannot()
    {
        var ct = TestContext.Current.CancellationToken;
        var migrated = await SeedAsync("awaiting-decision", "bob-1", ct);
        await BuildMigration().ApplyAsync(_persistence, ct);
        var unmigrated = await SeedAsync("awaiting-decision", "bob-1", ct);
        var service = BuildApprovalService();
        var number = AccreditationNumberResult.Success("A26ER5000270036WO");

        var refused = await service.RecordApprovedDeterminationAsync(unmigrated.Id, s_user, number, ct);
        var recorded = await service.RecordApprovedDeterminationAsync(migrated.Id, s_user, number, ct);

        Assert.False(refused.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, refused.FailureCode);
        Assert.Equal("awaiting-decision", (await _persistence.GetByIdAsync(unmigrated.Id, ct))!.StateId);

        Assert.True(recorded.IsSuccess, recorded.Message);
        var stored = (await _persistence.GetByIdAsync(migrated.Id, ct))!;
        Assert.Equal("approved-pending-contact", stored.StateId);
        Assert.Equal("A26ER5000270036WO", stored.Payload["accreditationId"].AsString);
        Assert.Equal("bob-1", stored.AssignedToId);
    }

    [Fact]
    public void It_is_registered_last_so_it_sets_the_final_version()
    {
        var services = new ServiceCollection();
        new ReAccreditationModule().RegisterServices(services, new ConfigurationBuilder().Build());

        var migrations = services
            .Where(d => d.ServiceType == typeof(IWorkItemMigration))
            .Select(d => d.ImplementationType)
            .ToList();
        var snapshotMigrations = migrations
            .Where(t => t is not null && typeof(ReAccreditationSnapshotMigrationBase).IsAssignableFrom(t))
            .ToList();

        Assert.Equal(typeof(ReAccreditationPendingContactSnapshotMigration), snapshotMigrations[^1]);
        Assert.True(
            migrations.IndexOf(typeof(ReAccreditationRemovePaymentReceivedSnapshotMigration))
            < migrations.IndexOf(typeof(ReAccreditationPendingContactSnapshotMigration)));
    }

    private ReAccreditationApprovalService BuildApprovalService() =>
        new(
            _persistence,
            new WorkItemRegistry([new ReAccreditationType()]),
            Substitute.For<IAccreditationNumberAdapter>(),
            Substitute.For<IBackgroundTaskQueue>(),
            [],
            NullLogger<ReAccreditationApprovalService>.Instance,
            Options.Create(new AccreditationConfig { CurrentYear = 2026 })
        );
}
