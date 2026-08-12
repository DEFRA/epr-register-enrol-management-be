using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-337: the continue-review service resolves the right
/// <c>continue-review-during-*</c> action from the work item's own
/// <c>resume-during-*</c> audit history (the inverse of what put it into
/// <c>updated</c>), and delegates the state change to the framework engine.
/// </summary>
public class ReAccreditationContinueReviewServiceTests
{
    private const string TenantClientId = "test-client";

    // --------------------------- happy path per state ---------------------------

    [Theory]
    [InlineData("resume-during-duly-making", "continue-review-during-duly-making")]
    [InlineData("resume-during-duly-made", "continue-review-during-duly-made")]
    [InlineData("resume-during-assessment", "continue-review-during-assessment")]
    [InlineData("resume-during-decision", "continue-review-during-decision")]
    public async Task ContinueReviewAsync_applies_the_inverse_action_for_the_resume(
        string resumeActionId,
        string expectedContinueActionId)
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(resumeActionId);

        var result = await harness.Service.ContinueReviewAsync(harness.WorkItem.Id, harness.User, ct);

        Assert.True(result.IsSuccess);
        await harness.Engine.Received(1).ApplyActionAsync(
            harness.WorkItem.Id, expectedContinueActionId, harness.User, ct);
    }

    [Fact]
    public async Task ContinueReviewAsync_uses_the_most_recent_action_applied_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("resume-during-assessment");
        // An earlier (stale) action-applied entry from a previous
        // query/resume cycle, recorded before the current one.
        harness.WorkItem.AuditLog.Insert(0, new WorkItemAuditEntry
        {
            Action = "action-applied",
            ActionDisplayName = "Action applied",
            CreatedAt = harness.WorkItem.AuditLog[0].CreatedAt.AddDays(-10),
            Details = new Dictionary<string, string?>
            {
                ["actionId"] = "resume-during-decision",
                ["fromStateId"] = "queried",
                ["toStateId"] = "updated",
            },
        });

        var result = await harness.Service.ContinueReviewAsync(harness.WorkItem.Id, harness.User, ct);

        Assert.True(result.IsSuccess);
        await harness.Engine.Received(1).ApplyActionAsync(
            harness.WorkItem.Id, "continue-review-during-assessment", harness.User, ct);
    }

    // ------------------------------- idempotency -------------------------------

    [Fact]
    public async Task ContinueReviewAsync_is_an_idempotent_replay_when_already_continued()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(resumeActionId: null, stateId: "submitted");

        var result = await harness.Service.ContinueReviewAsync(harness.WorkItem.Id, harness.User, ct);

        Assert.True(result.IsSuccess);
        Assert.True(result.IsIdempotentReplay);
        await harness.Engine.DidNotReceiveWithAnyArgs()
            .ApplyActionAsync(default, default!, default!, default);
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("withdrawn")]
    [InlineData("queried")]
    public async Task ContinueReviewAsync_fails_with_invalid_transition_when_not_updated(string stateId)
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(resumeActionId: null, stateId: stateId);

        var result = await harness.Service.ContinueReviewAsync(harness.WorkItem.Id, harness.User, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        await harness.Engine.DidNotReceiveWithAnyArgs()
            .ApplyActionAsync(default, default!, default!, default);
    }

    // --------------------------- audit history resolution ---------------------------

    [Fact]
    public async Task ContinueReviewAsync_fails_when_no_resolvable_action_applied_entry_exists()
    {
        var ct = TestContext.Current.CancellationToken;
        // 'updated' with no 'action-applied' entry at all — should not
        // happen via the real resume flow, but must not 500.
        var harness = new Harness(resumeActionId: null, stateId: "updated");

        var result = await harness.Service.ContinueReviewAsync(harness.WorkItem.Id, harness.User, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
    }

    // --------------------------------- gating ---------------------------------

    [Fact]
    public async Task ContinueReviewAsync_returns_not_found_when_the_work_item_does_not_exist()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("resume-during-duly-making", seedWorkItem: false);

        var result = await harness.Service.ContinueReviewAsync(harness.WorkItem.Id, harness.User, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
    }

    [Fact]
    public async Task ContinueReviewAsync_succeeds_for_a_work_item_not_submitted_by_the_caller()
    {
        // RBAC lives in the frontend now (ADR-0005).
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("resume-during-duly-making", submittedBy: "another-tenant");

        var result = await harness.Service.ContinueReviewAsync(harness.WorkItem.Id, harness.User, ct);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task ContinueReviewAsync_rejects_a_work_item_of_a_different_type()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("resume-during-duly-making", typeId: "some-other-type");

        var result = await harness.Service.ContinueReviewAsync(harness.WorkItem.Id, harness.User, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.UnknownAction, result.FailureCode);
    }

    [Fact]
    public async Task ContinueReviewAsync_propagates_an_engine_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("resume-during-duly-making");
        harness.Engine
            .ApplyActionAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>())
            .Returns(WorkItemActionResult.Failure(
                WorkItemActionFailureCode.MissingActorIdentity, "no user"));

        var result = await harness.Service.ContinueReviewAsync(harness.WorkItem.Id, harness.User, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.MissingActorIdentity, result.FailureCode);
    }

    [Fact]
    public async Task ContinueReviewAsync_rejects_null_user()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("resume-during-duly-making");

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            harness.Service.ContinueReviewAsync(harness.WorkItem.Id, null!, ct));
    }

    private sealed class Harness
    {
        public Harness(
            string? resumeActionId,
            string stateId = "updated",
            bool seedWorkItem = true,
            string typeId = ReAccreditationType.Id,
            string submittedBy = TenantClientId)
        {
            WorkItem = new WorkItem
            {
                TypeId = typeId,
                StateId = stateId,
                SubmittedBy = submittedBy,
            };

            if (resumeActionId is not null)
            {
                WorkItem.AuditLog.Add(new WorkItemAuditEntry
                {
                    Action = "action-applied",
                    ActionDisplayName = "Action applied",
                    CreatedAt = DateTime.UtcNow.AddHours(-1),
                    Details = new Dictionary<string, string?>
                    {
                        ["actionId"] = resumeActionId,
                        ["fromStateId"] = "queried",
                        ["toStateId"] = "updated",
                    },
                });
            }

            Persistence = Substitute.For<IWorkItemPersistence>();
            Persistence
                .GetByIdAsync(WorkItem.Id, Arg.Any<CancellationToken>())
                .Returns(seedWorkItem ? WorkItem : null);

            Engine = Substitute.For<IWorkItemService>();
            Engine
                .ApplyActionAsync(
                    Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(),
                    Arg.Any<CancellationToken>())
                .Returns(WorkItemActionResult.Success(WorkItem));

            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("user:id", "alice-1"),
                    new Claim("user:name", "Alice Example"),
                    new Claim("client_id", TenantClientId),
                ],
                "test"));

            Service = new ReAccreditationContinueReviewService(
                Persistence,
                Engine,
                NullLogger<ReAccreditationContinueReviewService>.Instance);
        }

        public WorkItem WorkItem { get; }
        public IWorkItemPersistence Persistence { get; }
        public IWorkItemService Engine { get; }
        public ClaimsPrincipal User { get; }
        public ReAccreditationContinueReviewService Service { get; }
    }
}
