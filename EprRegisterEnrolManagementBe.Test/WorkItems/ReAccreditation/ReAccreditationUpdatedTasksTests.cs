using System.Security.Claims;
using EprRegisterEnrolManagementBe.Config;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Notifications;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.Utils.Background;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-372 acceptance, driven through the real <see cref="ReAccreditationType"/>
/// template and the real resolver rather than test doubles.
///
/// The bug: a regulator queries an application mid-review, the operator
/// responds, the application lands in <c>updated</c> — and the regulator is
/// shown an empty task list, with no way to finish the review and no sight of
/// the progress already made.
/// </summary>
public class ReAccreditationUpdatedTasksTests
{
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

    /// <summary>
    /// A re-accreditation work item as it exists after resume-from-query: in
    /// <c>updated</c>, carrying the frozen v10 snapshot every live item has,
    /// with the resume-during-* entry that records where it came from.
    /// </summary>
    private static WorkItem BuildUpdatedWorkItem(
        string resumeActionId,
        Dictionary<string, HashSet<string>>? completed = null
    )
    {
        var workItem = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "updated",
            SubmittedBy = "test-client",
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType()),
        };
        workItem.AuditLog.Add(
            new WorkItemAuditEntry
            {
                Action = "action-applied",
                ActionDisplayName = "Action applied",
                CreatedAt = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc),
                Details = new Dictionary<string, string?>
                {
                    ["actionId"] = resumeActionId,
                    ["fromStateId"] = "queried",
                    ["toStateId"] = "updated",
                },
            }
        );

        foreach (var (stateId, taskIds) in completed ?? [])
        {
            workItem.CompletedTaskIdsByState[stateId] = new(taskIds, StringComparer.OrdinalIgnoreCase);
        }

        return workItem;
    }

    private static (WorkItemService Engine, IWorkItemPersistence Persistence) BuildEngine(
        WorkItem workItem,
        params IWorkItemPostTaskHook[] postTaskHooks
    )
    {
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var engine = new WorkItemService(
            new WorkItemRegistry([new ReAccreditationType()]),
            persistence,
            NullLogger<WorkItemService>.Instance,
            postTaskHooks: postTaskHooks,
            taskStateResolvers: [new ReAccreditationTaskStateResolver()]
        );

        return (engine, persistence);
    }

    // ------------------------------ AC1 ------------------------------

    /// <summary>
    /// AC1: the projected task list while in <c>updated</c> is the originating
    /// state's, not empty.
    /// </summary>
    [Theory]
    [InlineData(
        "resume-during-duly-making",
        new[] { "verify-organisation-details", "confirm-application-completeness" }
    )]
    [InlineData("resume-during-duly-made", new[] { "confirm-registration-fee-paid" })]
    [InlineData(
        "resume-during-assessment",
        new[] { "review-compliance-history", "assess-technical-capacity", "assess-financial-capacity" }
    )]
    [InlineData("resume-during-decision", new[] { "record-decision-rationale" })]
    public void An_updated_item_projects_the_originating_states_tasks(
        string resumeActionId,
        string[] expectedTaskIds
    )
    {
        var workItem = BuildUpdatedWorkItem(resumeActionId);
        var (engine, _) = BuildEngine(workItem);

        var projection = engine.Project(workItem);

        Assert.Equal(expectedTaskIds, projection.Tasks.Select(t => t.TaskId));
        // The item has not moved — only the checklist it shows has changed.
        Assert.Equal("updated", projection.WorkItem.StateId);
    }

    [Fact]
    public void An_updated_item_still_offers_only_the_actions_that_leave_updated()
    {
        var workItem = BuildUpdatedWorkItem("resume-during-assessment");
        var (engine, _) = BuildEngine(workItem);

        var projection = engine.Project(workItem);

        // continue-review-during-* are CallerInvocable: false but still
        // declared from 'updated'; withdraw-during-updated is the caller-
        // facing one. Crucially none of the originating state's own actions
        // (e.g. submit-for-decision) leak in.
        Assert.All(projection.AvailableActions, a => Assert.Equal("updated", a.FromStateId));
        Assert.Contains("withdraw-during-updated", projection.AvailableActions.Select(a => a.ActionId));
    }

    // ------------------------------ AC2 ------------------------------

    /// <summary>
    /// AC2: work completed before the query is still shown as complete.
    /// </summary>
    [Fact]
    public void Progress_made_before_the_query_is_still_visible_while_updated()
    {
        var workItem = BuildUpdatedWorkItem(
            "resume-during-assessment",
            completed: new()
            {
                ["assessment-in-progress"] = ["review-compliance-history", "assess-technical-capacity"],
            }
        );
        var (engine, _) = BuildEngine(workItem);

        var projection = engine.Project(workItem);

        Assert.True(projection.Tasks.Single(t => t.TaskId == "review-compliance-history").IsComplete);
        Assert.True(projection.Tasks.Single(t => t.TaskId == "assess-technical-capacity").IsComplete);
        Assert.False(projection.Tasks.Single(t => t.TaskId == "assess-financial-capacity").IsComplete);
    }

    // ------------------------------ AC3 ------------------------------

    /// <summary>
    /// AC3, the heart of the story: the regulator finishes the outstanding
    /// tasks while the item is in <c>updated</c>, continue-review carries it
    /// back, and the work is still done.
    /// </summary>
    [Fact]
    public async Task Tasks_completed_while_updated_survive_the_return_to_the_originating_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildUpdatedWorkItem(
            "resume-during-assessment",
            completed: new() { ["assessment-in-progress"] = ["review-compliance-history"] }
        );
        var (engine, persistence) = BuildEngine(workItem);

        // The regulator finishes the remaining two assessment tasks while the
        // application is parked in 'updated'.
        Assert.True(
            (await engine.CompleteTaskAsync(workItem.Id, "assess-technical-capacity", s_user, ct))
                .IsSuccess
        );
        Assert.True(
            (
                await engine.SetTaskStatusAsync(
                    workItem.Id,
                    "assess-financial-capacity",
                    WorkItemTaskStatus.Completed,
                    s_user,
                    ct
                )
            ).IsSuccess
        );

        // Both landed in the originating state's bucket, not a bucket keyed
        // on 'updated' that nothing would ever read again.
        Assert.False(workItem.CompletedTaskIdsByState.ContainsKey("updated"));

        // Continue review carries it back to where it was queried from.
        var continueReview = new ReAccreditationContinueReviewService(
            persistence,
            engine,
            NullLogger<ReAccreditationContinueReviewService>.Instance
        );
        Assert.True((await continueReview.ContinueReviewAsync(workItem.Id, s_user, ct)).IsSuccess);
        Assert.Equal("assessment-in-progress", workItem.StateId);

        // Everything the regulator did is still done, and the gated onward
        // action is now genuinely available.
        var projection = engine.Project(workItem);
        Assert.All(projection.Tasks, t => Assert.True(t.IsComplete));
        Assert.Contains("submit-for-decision", projection.AvailableActions.Select(a => a.ActionId));

        // ...and it really can be applied: the all-tasks-complete gate reads
        // the same bucket the work was written to.
        Assert.True(
            (await engine.ApplyActionAsync(workItem.Id, "submit-for-decision", s_user, ct)).IsSuccess
        );
        Assert.Equal("awaiting-decision", workItem.StateId);
    }

    [Fact]
    public async Task A_task_from_a_state_other_than_the_originating_one_is_still_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildUpdatedWorkItem("resume-during-assessment");
        var (engine, _) = BuildEngine(workItem);

        // 'record-decision-rationale' belongs to awaiting-decision, not to
        // the assessment stage this item was queried from.
        var result = await engine.CompleteTaskAsync(
            workItem.Id,
            "record-decision-rationale",
            s_user,
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.TaskNotApplicable, result.FailureCode);
    }

    // --------------------------- auto-transition ---------------------------

    /// <summary>
    /// The duly-made hook is the only way an application leaves
    /// <c>submitted</c> — <c>duly-make</c> is not a caller-invocable action.
    /// So when the query was raised during duly-making, completing the last
    /// submitted-state task while in <c>updated</c> must still fire it.
    /// Suppressing it would drop the item into <c>submitted</c> with every box
    /// already ticked and nothing left that could ever advance it.
    /// </summary>
    [Fact]
    public async Task Finishing_the_duly_making_checklist_while_updated_still_marks_it_duly_made()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildUpdatedWorkItem(
            "resume-during-duly-making",
            completed: new() { ["submitted"] = ["verify-organisation-details"] }
        );
        var harness = new DulyMadeHarness(workItem);
        var (engine, persistence) = BuildEngine(workItem, harness.Hook);

        await engine.CompleteTaskAsync(workItem.Id, "confirm-application-completeness", s_user, ct);

        // The real ReAccreditationDulyMadeHook ran end to end, not a stand-in:
        // the application is duly made and its SLA clock has started.
        Assert.Equal("duly-made", workItem.StateId);
        Assert.NotNull(workItem.SlaClock);

        // Every edge the item traversed is one the template declares. The
        // waypoint was discharged via continue-review-during-duly-making
        // rather than jumping updated → duly-made — an edge
        // ReAccreditationType does not declare and which neither
        // management-fe nor the journey tests model.
        var applied = workItem
            .AuditLog.Where(e => e.Action == "action-applied")
            .Select(e =>
                (
                    ActionId: e.Details["actionId"],
                    From: e.Details["fromStateId"],
                    To: e.Details["toStateId"]
                )
            )
            .ToList();

        Assert.Equal(
            [
                ("resume-during-duly-making", "queried", "updated"),
                ("continue-review-during-duly-making", "updated", "submitted"),
                ("duly-make", "submitted", "duly-made"),
            ],
            applied
        );
        Assert.DoesNotContain(applied, e => e.From == "updated" && e.To == "duly-made");

        // The from-state that goes on the wire to the operator backend is
        // 'submitted', never the unmodelled 'updated'. Exactly one push: the
        // waypoint discharge shares the duly-made save — it cannot have a save
        // of its own, see ReAccreditationUpdatedWaypointPersistenceTests — and
        // the push necessarily runs after that save, by which point the item is
        // already duly-made, so a separate discharge push could only misreport
        // where it ended.
        Assert.Equal([("duly-make", "submitted")], harness.Pushes);

        // A caseworker who presses Continue review after the auto-advance is
        // not punished for it: the item has already reached a valid continue
        // target, so this is an idempotent replay rather than an error.
        var continueReview = new ReAccreditationContinueReviewService(
            persistence,
            engine,
            NullLogger<ReAccreditationContinueReviewService>.Instance
        );
        var replay = await continueReview.ContinueReviewAsync(workItem.Id, s_user, ct);

        Assert.True(replay.IsSuccess);
        Assert.True(replay.IsIdempotentReplay);
        Assert.Equal("duly-made", workItem.StateId);
    }

    /// <summary>
    /// The other three originating states have no post-task hook, so the item
    /// stays in <c>updated</c> until a caseworker explicitly continues the
    /// review. Nothing auto-fires and nothing is skipped.
    /// </summary>
    [Theory]
    [InlineData("resume-during-duly-made", "confirm-registration-fee-paid")]
    [InlineData("resume-during-decision", "record-decision-rationale")]
    public async Task Finishing_the_checklist_for_other_origins_leaves_the_item_in_updated(
        string resumeActionId,
        string finalTaskId
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildUpdatedWorkItem(resumeActionId);
        var (engine, _) = BuildEngine(workItem, new ReAccreditationDulyMadeHookStandIn());

        await engine.CompleteTaskAsync(workItem.Id, finalTaskId, s_user, ct);

        Assert.Equal("updated", workItem.StateId);
    }

    // ------------------------------ AC4 ------------------------------

    /// <summary>
    /// AC4: no regression to <c>queried</c>, which is deliberately out of
    /// scope. An application awaiting an operator response has nothing
    /// outstanding for the regulator.
    /// </summary>
    [Fact]
    public void A_queried_item_still_projects_no_tasks()
    {
        var workItem = BuildUpdatedWorkItem("resume-during-assessment");
        workItem.StateId = "queried";
        var (engine, _) = BuildEngine(workItem);

        Assert.Empty(engine.Project(workItem).Tasks);
    }

    /// <summary>
    /// AC4: leaving <c>updated</c> is not gated on task completion, and never
    /// was. An operator must be able to withdraw an application whose review
    /// is half-finished.
    /// </summary>
    [Fact]
    public async Task Withdraw_during_updated_still_works_with_outstanding_tasks()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildUpdatedWorkItem("resume-during-assessment");
        var (engine, _) = BuildEngine(workItem);

        // Precondition: the checklist really is outstanding now that we
        // project one at all.
        Assert.Contains(engine.Project(workItem).Tasks, t => !t.IsComplete);

        var result = await engine.ApplyActionAsync(workItem.Id, "withdraw-during-updated", s_user, ct);

        Assert.True(result.IsSuccess);
        Assert.Equal("withdrawn", workItem.StateId);
    }

    /// <summary>
    /// AC4: continue-review is likewise ungated — a caseworker can carry an
    /// application back into review before every box is ticked.
    /// </summary>
    [Fact]
    public async Task Continue_review_still_works_with_outstanding_tasks()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildUpdatedWorkItem("resume-during-assessment");
        var (engine, persistence) = BuildEngine(workItem);
        var continueReview = new ReAccreditationContinueReviewService(
            persistence,
            engine,
            NullLogger<ReAccreditationContinueReviewService>.Instance
        );

        Assert.True((await continueReview.ContinueReviewAsync(workItem.Id, s_user, ct)).IsSuccess);
        Assert.Equal("assessment-in-progress", workItem.StateId);
    }

    /// <summary>
    /// AC4: the template is unchanged — no version bump and no migration were
    /// needed, because the fix resolves at projection time against the
    /// snapshot every in-flight item already carries.
    /// </summary>
    [Fact]
    public void The_template_version_is_unchanged()
    {
        Assert.Equal("v10", new ReAccreditationType().TemplateVersion);
        Assert.Empty(new ReAccreditationType().GetTasksForState("updated"));
    }

    // ----------------- RA-346 approve gating x RA-372 redirect -----------------

    /// <summary>
    /// Approve is refused outright while the item is in <c>updated</c> — and
    /// refused for being in the wrong <em>state</em>, not waved through.
    /// RA-346's gate is satisfied vacuously by any state declaring no tasks,
    /// and <c>updated</c> declares none, so if that state check ever moved
    /// after the task check this would silently start approving applications
    /// mid-query. This pins the ordering.
    /// </summary>
    [Fact]
    public async Task An_updated_item_cannot_be_approved_even_though_updated_declares_no_tasks()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildUpdatedWorkItem("resume-during-decision");
        var (_, persistence) = BuildEngine(workItem);

        var result = await BuildApprovalService(persistence).ApproveAsync(workItem.Id, s_user, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        Assert.Equal("updated", workItem.StateId);
    }

    /// <summary>
    /// The combined happy path, and the point of RA-372 for this origin: the
    /// regulator completes <c>record-decision-rationale</c> while the item is
    /// in <c>updated</c>, continue-review carries it back to
    /// <c>awaiting-decision</c>, and RA-346's approve gate is satisfied by the
    /// work done in the waypoint.
    /// </summary>
    [Fact]
    public async Task Work_done_in_updated_satisfies_the_approve_gate_after_continue_review()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildUpdatedWorkItem("resume-during-decision");
        var (engine, persistence) = BuildEngine(workItem);
        var continueReview = new ReAccreditationContinueReviewService(
            persistence,
            engine,
            NullLogger<ReAccreditationContinueReviewService>.Instance
        );

        Assert.True(
            (
                await engine.CompleteTaskAsync(workItem.Id, "record-decision-rationale", s_user, ct)
            ).IsSuccess
        );
        Assert.True((await continueReview.ContinueReviewAsync(workItem.Id, s_user, ct)).IsSuccess);
        Assert.Equal("awaiting-decision", workItem.StateId);

        var result = await BuildApprovalService(persistence).ApproveAsync(workItem.Id, s_user, ct);

        Assert.True(result.IsSuccess);
        Assert.Equal("approved", workItem.StateId);
    }

    /// <summary>
    /// The inverse: RA-372 must not weaken RA-346. An item back in
    /// <c>awaiting-decision</c> with its checklist still outstanding is still
    /// refused, with the tasks-incomplete contract intact.
    /// </summary>
    [Fact]
    public async Task Approve_is_still_refused_when_the_decision_task_was_never_completed()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildUpdatedWorkItem("resume-during-decision");
        var (engine, persistence) = BuildEngine(workItem);
        var continueReview = new ReAccreditationContinueReviewService(
            persistence,
            engine,
            NullLogger<ReAccreditationContinueReviewService>.Instance
        );

        Assert.True((await continueReview.ContinueReviewAsync(workItem.Id, s_user, ct)).IsSuccess);
        Assert.Equal("awaiting-decision", workItem.StateId);

        var result = await BuildApprovalService(persistence).ApproveAsync(workItem.Id, s_user, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.IncompleteTasks, result.FailureCode);
        Assert.Contains("awaiting-decision", result.Message);
    }

    private static ReAccreditationApprovalService BuildApprovalService(
        IWorkItemPersistence persistence
    )
    {
        var idGenerator = Substitute.For<IAccreditationIdGenerator>();
        idGenerator
            .GenerateAsync(Arg.Any<BsonDocument>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns("ACC-2027-X-TEST0000");

        return new ReAccreditationApprovalService(
            persistence,
            new WorkItemRegistry([new ReAccreditationType()]),
            idGenerator,
            Substitute.For<IBackgroundTaskQueue>(),
            [],
            NullLogger<ReAccreditationApprovalService>.Instance,
            Options.Create(new AccreditationConfig { CurrentYear = 2027 })
        );
    }

    // ------------------------------- doubles -------------------------------

    /// <summary>
    /// Wires up the genuine <see cref="ReAccreditationDulyMadeHook"/> — real
    /// status-push hook included — so the auto-advance is proved by the code
    /// that actually runs in production rather than by a stand-in. Records
    /// what reached the operator-backend push adapter, since the from-state on
    /// that wire is the thing the undeclared edge would have corrupted.
    /// </summary>
    private sealed class DulyMadeHarness
    {
        public List<(string ActionId, string FromStateId)> Pushes { get; } = [];

        public ReAccreditationDulyMadeHook Hook { get; }

        public DulyMadeHarness(WorkItem workItem)
        {
            var persistence = Substitute.For<IWorkItemPersistence>();
            persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

            var notifyClient = Substitute.For<INotifyClient>();
            notifyClient
                .SendEmailAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<Dictionary<string, string>>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(NotifySendResult.Success("msg-id"));

            var pushAdapter = Substitute.For<IOperatorBackendPushAdapter>();
            pushAdapter
                .PushStatusChangedAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<DateTime>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(call =>
                {
                    Pushes.Add((call.ArgAt<string>(5), call.ArgAt<string>(2)));
                    return OperatorBackendPushResult.Skipped("test");
                });

            var auditAppender = Substitute.For<IWorkItemAuditAppender>();

            Hook = new ReAccreditationDulyMadeHook(
                persistence,
                notifyClient,
                auditAppender,
                new ReAccreditationStatusPushHook(
                    pushAdapter,
                    auditAppender,
                    NullLogger<ReAccreditationStatusPushHook>.Instance
                ),
                TimeProvider.System,
                NullLogger<ReAccreditationDulyMadeHook>.Instance
            );
        }
    }

    private sealed class RecordingPostTaskHook : IWorkItemPostTaskHook
    {
        public List<string> StateIds { get; } = [];

        public Task OnAllTasksCompletedAsync(
            WorkItem workItem,
            string stateId,
            ClaimsPrincipal user,
            CancellationToken cancellationToken
        )
        {
            StateIds.Add(stateId);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Mirrors the real <see cref="ReAccreditationDulyMadeHook"/>'s guard
    /// (only acts on <c>submitted</c>) without its notification/persistence
    /// dependencies, so a test can prove nothing fires for the other origins.
    /// </summary>
    private sealed class ReAccreditationDulyMadeHookStandIn : IWorkItemPostTaskHook
    {
        public Task OnAllTasksCompletedAsync(
            WorkItem workItem,
            string stateId,
            ClaimsPrincipal user,
            CancellationToken cancellationToken
        )
        {
            if (string.Equals(stateId, "submitted", StringComparison.OrdinalIgnoreCase))
            {
                workItem.StateId = "duly-made";
            }
            return Task.CompletedTask;
        }
    }
}
