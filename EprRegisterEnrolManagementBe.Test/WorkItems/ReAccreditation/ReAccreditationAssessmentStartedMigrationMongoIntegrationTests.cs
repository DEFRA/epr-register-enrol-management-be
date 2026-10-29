using System.Security.Claims;
using EprRegisterEnrolManagementBe.Config;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.Utils.Background;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-527 follow-up (v17 → v18): runs the assessment-started rules migration
/// against a real ephemeral Mongo, on v17 fixtures in a spread of states
/// (including cases already in <c>assessment-in-progress</c>). It must add the
/// <c>assessment-started</c> state, retarget <c>start-assessment</c> to it and
/// add its four exits, set v18, leave every other stored field exactly as it
/// was, write nothing on a second run, give a partially migrated snapshot only
/// what it lacks, and leave a migrated Duly made case able to start into
/// <c>assessment-started</c> and take every exit from it.
/// </summary>
public sealed class ReAccreditationAssessmentStartedMigrationMongoIntegrationTests
    : IAsyncDisposable
{
    private const string StartedStateId = "assessment-started";

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

    public ReAccreditationAssessmentStartedMigrationMongoIntegrationTests(
        MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("reaccred-assessment-started");
        _clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _persistence = new WorkItemPersistence(_clientFactory, NullLoggerFactory.Instance);
    }

    public async ValueTask DisposeAsync() =>
        await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);

    private static ReAccreditationAssessmentStartedSnapshotMigration BuildMigration() =>
        new(NullLogger<ReAccreditationAssessmentStartedSnapshotMigration>.Instance);

    private static WorkItemTemplateSnapshot Live() =>
        WorkItemTemplateSnapshot.Capture(new ReAccreditationType());

    private static List<WorkItemTransition> Sorted(IEnumerable<WorkItemTransition> transitions) =>
        transitions.OrderBy(t => t.ActionId).ThenBy(t => t.FromStateId).ToList();

    private async Task<WorkItem> SeedAsync(
        string stateId,
        string? assignedToId,
        CancellationToken ct,
        WorkItemTemplateSnapshot? snapshot = null)
    {
        snapshot ??= ReAccreditationAssessmentStartedSnapshotMigrationTests.BuildV17Snapshot();
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
            TemplateVersion = snapshot.TemplateVersion,
            TemplateSnapshot = snapshot,
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
            Details = new Dictionary<string, string?> { ["actionId"] = "start-assessment" },
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
            { "duly-made", null },
            { "assessment-in-progress", "bob-1" },
            { "assessment-in-progress", null },
            { "awaiting-decision", "bob-1" },
            { "queried", "bob-1" },
            { "updated", null },
            { "approved-pending-contact", "bob-1" },
            { "approved", "bob-1" },
            { "rejected", null },
            { "withdrawn", null },
        };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Migration_adds_the_assessment_started_rules_sets_v18_and_changes_nothing_else(
        string stateId,
        string? assignedToId)
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedAsync(stateId, assignedToId, ct);
        var before = (await _persistence.GetByIdAsync(seeded.Id, ct))!;

        await BuildMigration().ApplyAsync(_persistence, ct);

        var after = (await _persistence.GetByIdAsync(seeded.Id, ct))!;
        Assert.Equal("v18", after.TemplateVersion);
        Assert.Equal("v18", after.TemplateSnapshot!.TemplateVersion);

        var started = Assert.Single(after.TemplateSnapshot.States, s => s.Id == StartedStateId);
        Assert.Equal("Duly made", started.DisplayName);
        Assert.False(started.IsTerminal);

        var start = Assert.Single(after.TemplateSnapshot.Transitions, t => t.ActionId == "start-assessment");
        Assert.Equal("duly-made", start.FromStateId);
        Assert.Equal(StartedStateId, start.ToStateId);
        Assert.Equal("Assessment started", start.DisplayName);
        Assert.True(start.CallerInvocable);

        var exits = after.TemplateSnapshot.Transitions.Where(t => t.FromStateId == StartedStateId).ToList();
        Assert.Equal(4, exits.Count);
        AssertExit(exits, "submit-for-decision", "awaiting-decision", callerInvocable: false);
        AssertExit(exits, "query-during-assessment", "queried", callerInvocable: true);
        AssertExit(exits, "withdraw-during-assessment", "withdrawn", callerInvocable: true);
        AssertExit(exits, "sla-extend", "assessment-in-progress", callerInvocable: false);

        // The migrated rules are exactly the live v18 rules, states in declaration order.
        var live = Live();
        Assert.Equal(live.States, after.TemplateSnapshot.States);
        Assert.Equal(Sorted(live.Transitions), Sorted(after.TemplateSnapshot.Transitions));

        // Every other rule is untouched.
        Assert.Equal(
            before.TemplateSnapshot!.Transitions.Where(t => t.ActionId != "start-assessment").ToList(),
            after.TemplateSnapshot.Transitions
                .Where(t => t.ActionId != "start-assessment" && t.FromStateId != StartedStateId)
                .ToList());
        Assert.Equal(
            before.TemplateSnapshot.States,
            after.TemplateSnapshot.States.Where(s => s.Id != StartedStateId).ToList());
        Assert.Equal(before.Version + 1, after.Version);

        // Field by field, the preserved things: a case in assessment-in-progress stays there.
        Assert.Equal(stateId, after.StateId);
        Assert.Equal(assignedToId, after.AssignedToId);
        Assert.Equal(before.AssignedToName, after.AssignedToName);
        Assert.Equal(before.AssignedAt, after.AssignedAt);
        Assert.Equal(before.LastModifiedAt, after.LastModifiedAt);
        Assert.Equal(before.Payload, after.Payload);
        Assert.Equal(before.SlaClock!.StartedAt, after.SlaClock!.StartedAt);
        Assert.Equal(before.SlaClock.TargetDuration, after.SlaClock.TargetDuration);
        Assert.Single(after.Notes);
        Assert.Equal(before.AuditLog.Count, after.AuditLog.Count);

        // Everything else: once the three fields the migration owns are put
        // back, the stored document is identical.
        after.TemplateSnapshot = before.TemplateSnapshot;
        after.TemplateVersion = before.TemplateVersion;
        after.Version = before.Version;
        Assert.Equal(before.ToBsonDocument(), after.ToBsonDocument());
    }

    private static void AssertExit(
        List<WorkItemTransition> exits,
        string actionId,
        string toStateId,
        bool callerInvocable)
    {
        var exit = Assert.Single(exits, t => t.ActionId == actionId);
        Assert.Equal(toStateId, exit.ToStateId);
        Assert.Equal(callerInvocable, exit.CallerInvocable);
    }

    [Fact]
    public async Task A_second_run_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = new[]
        {
            await SeedAsync("duly-made", "bob-1", ct),
            await SeedAsync("assessment-in-progress", null, ct),
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
    public async Task An_item_already_on_the_v18_rules_is_not_written()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = await SeedAsync(StartedStateId, "bob-1", ct, Live());
        var before = (await _persistence.GetByIdAsync(item.Id, ct))!;

        await BuildMigration().ApplyAsync(_persistence, ct);

        var after = (await _persistence.GetByIdAsync(item.Id, ct))!;
        Assert.Equal(before.ToBsonDocument(), after.ToBsonDocument());
    }

    /// <summary>
    /// v18 grew in place while unreleased: an item snapshotted by the task 002
    /// build has the state, the retargeted start and submit-for-decision; task
    /// 005 added sla-extend and task 006 the query. It gains only what it
    /// lacks, and nothing it already had moves.
    /// </summary>
    [Theory]
    [InlineData("task-002", new[] { "sla-extend", "query-during-assessment", "withdraw-during-assessment" })]
    [InlineData("task-005", new[] { "query-during-assessment", "withdraw-during-assessment" })]
    [InlineData("task-006", new[] { "withdraw-during-assessment" })]
    public async Task A_partially_migrated_item_gains_only_what_it_lacks(
        string build,
        string[] missingExits)
    {
        _ = build;
        var ct = TestContext.Current.CancellationToken;
        var live = Live();
        var partial = new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v18",
            States = live.States,
            Transitions = live
                .Transitions.Where(t =>
                    !(t.FromStateId == StartedStateId && missingExits.Contains(t.ActionId)))
                .ToList(),
        };
        var item = await SeedAsync(StartedStateId, "bob-1", ct, partial);
        var before = (await _persistence.GetByIdAsync(item.Id, ct))!;

        await BuildMigration().ApplyAsync(_persistence, ct);

        var after = (await _persistence.GetByIdAsync(item.Id, ct))!;
        Assert.Equal(live.States, after.TemplateSnapshot!.States);
        Assert.Equal(
            partial.Transitions,
            after.TemplateSnapshot.Transitions.Take(partial.Transitions.Count).ToList());
        Assert.Equal(
            missingExits.Order().ToList(),
            after.TemplateSnapshot.Transitions
                .Skip(partial.Transitions.Count)
                .Select(t => t.ActionId)
                .Order()
                .ToList());
        Assert.Equal(Sorted(live.Transitions), Sorted(after.TemplateSnapshot.Transitions));
        Assert.Equal(before.Version + 1, after.Version);

        after.TemplateSnapshot = before.TemplateSnapshot;
        after.Version = before.Version;
        Assert.Equal(before.ToBsonDocument(), after.ToBsonDocument());
    }

    [Fact]
    public async Task An_unmigrated_duly_made_case_still_starts_into_assessment_in_progress()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = await SeedAsync("duly-made", "bob-1", ct);

        var result = await BuildEngine().ApplyActionAsync(item.Id, "start-assessment", s_user, ct);

        // Positive control for the test below: the v17 rules still land on "Updated".
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("assessment-in-progress", (await _persistence.GetByIdAsync(item.Id, ct))!.StateId);
    }

    [Theory]
    [InlineData("determination", "rejected-pending-contact")]
    [InlineData("query", "queried")]
    [InlineData("withdraw", "withdrawn")]
    [InlineData("deadline", "assessment-in-progress")]
    public async Task A_migrated_duly_made_case_starts_into_assessment_started_and_every_exit_works(
        string exit,
        string expectedStateId)
    {
        var ct = TestContext.Current.CancellationToken;
        var item = await SeedAsync("duly-made", "bob-1", ct);
        await BuildMigration().ApplyAsync(_persistence, ct);
        var engine = BuildEngine();

        var start = await engine.ApplyActionAsync(item.Id, "start-assessment", s_user, ct);

        Assert.True(start.IsSuccess, start.Message);
        Assert.Equal(StartedStateId, (await _persistence.GetByIdAsync(item.Id, ct))!.StateId);

        var (succeeded, message) = exit switch
        {
            "determination" => Outcome(await BuildDecisionService(engine).LogDecisionAsync(
                item.Id, ReAccreditationDecisionOutcome.Rejected, s_user, ct)),
            "query" => Outcome(await BuildQueryService(engine).QueryAsync(
                item.Id, ["business-plan"], "Please confirm the tonnage figures.", s_user, ct)),
            "withdraw" => Outcome(await new ReAccreditationWithdrawService(
                _persistence, engine, NullLogger<ReAccreditationWithdrawService>.Instance)
                .WithdrawAsync(item.Id, "No longer needed", s_user, ct)),
            "deadline" => Outcome(await BuildSlaService().ExtendAsync(
                item.Id, TimeSpan.FromDays(14), "More time needed", s_user, ct)),
            _ => throw new ArgumentOutOfRangeException(nameof(exit), exit, null),
        };

        Assert.True(succeeded, message);
        Assert.Equal(expectedStateId, (await _persistence.GetByIdAsync(item.Id, ct))!.StateId);
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

        Assert.Equal(typeof(ReAccreditationAssessmentStartedSnapshotMigration), snapshotMigrations[^1]);
        Assert.True(
            migrations.IndexOf(typeof(ReAccreditationPendingContactSnapshotMigration))
            < migrations.IndexOf(typeof(ReAccreditationAssessmentStartedSnapshotMigration)));
    }

    private static (bool Succeeded, string? Message) Outcome(WorkItemActionResult result) =>
        (result.IsSuccess, result.Message);

    private static (bool Succeeded, string? Message) Outcome(SlaActionResult result) =>
        (result.IsSuccess, result.Message);

    private static WorkItemRegistry Registry() => new([new ReAccreditationType()]);

    private WorkItemService BuildEngine() =>
        new(Registry(), _persistence, NullLogger<WorkItemService>.Instance);

    private ReAccreditationLogDecisionService BuildDecisionService(WorkItemService engine) =>
        new(
            _persistence,
            engine,
            new ReAccreditationApprovalService(
                _persistence,
                Registry(),
                Substitute.For<IAccreditationNumberAdapter>(),
                Substitute.For<IBackgroundTaskQueue>(),
                [],
                NullLogger<ReAccreditationApprovalService>.Instance,
                Options.Create(new AccreditationConfig { CurrentYear = 2026 })
            ),
            new ReAccreditationRefusalService(
                _persistence,
                Registry(),
                [],
                NullLogger<ReAccreditationRefusalService>.Instance
            ),
            NullLogger<ReAccreditationLogDecisionService>.Instance
        );

    private ReAccreditationQueryService BuildQueryService(WorkItemService engine) =>
        new(
            _persistence,
            engine,
            new WorkItemAuditAppender(_persistence, NullLogger<WorkItemAuditAppender>.Instance),
            NullLogger<ReAccreditationQueryService>.Instance,
            new FakeTimeProvider(s_now)
        );

    private SlaService BuildSlaService() =>
        new(
            _persistence,
            NullLogger<SlaService>.Instance,
            new FakeTimeProvider(s_now),
            registry: Registry()
        );
}
