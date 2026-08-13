using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-252: the withdraw service resolves the right
/// <c>withdraw</c>/<c>withdraw-during-*</c> action from the work item's
/// current state, records the operator's reason as a note before the
/// transition, and delegates the state change to the framework engine.
/// </summary>
public class ReAccreditationWithdrawServiceTests
{
    private const string TenantClientId = "test-client";
    private const string Reason = "No longer required for this accreditation cycle";

    // -------------------------- action resolution --------------------------

    [Theory]
    [InlineData("submitted", "withdraw")]
    [InlineData("duly-made", "withdraw-during-duly-made")]
    [InlineData("assessment-in-progress", "withdraw-during-assessment")]
    [InlineData("awaiting-decision", "withdraw-during-decision")]
    [InlineData("queried", "withdraw-during-query")]
    [InlineData("updated", "withdraw-during-updated")]
    // Case-insensitive: state ids are compared the same way the engine does.
    [InlineData("SUBMITTED", "withdraw")]
    public void ResolveWithdrawActionId_maps_each_withdrawable_state(
        string stateId,
        string expected
    )
    {
        Assert.Equal(expected, ReAccreditationWithdrawService.ResolveWithdrawActionId(stateId));
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("withdrawn")]
    [InlineData("something-else")]
    [InlineData(null)]
    public void ResolveWithdrawActionId_returns_null_for_a_non_withdrawable_state(string? stateId)
    {
        Assert.Null(ReAccreditationWithdrawService.ResolveWithdrawActionId(stateId));
    }

    // ------------------------------ WithdrawAsync ------------------------------

    [Theory]
    [InlineData("submitted", "withdraw")]
    [InlineData("duly-made", "withdraw-during-duly-made")]
    [InlineData("assessment-in-progress", "withdraw-during-assessment")]
    [InlineData("awaiting-decision", "withdraw-during-decision")]
    [InlineData("queried", "withdraw-during-query")]
    [InlineData("updated", "withdraw-during-updated")]
    public async Task WithdrawAsync_applies_the_action_for_the_current_state(
        string stateId,
        string expectedActionId
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId);

        var result = await harness.Service.WithdrawAsync(
            harness.WorkItem.Id,
            Reason,
            harness.User,
            ct
        );

        Assert.True(result.IsSuccess);
        await harness
            .Engine.Received(1)
            .ApplyActionAsync(harness.WorkItem.Id, expectedActionId, harness.User, ct);
    }

    [Fact]
    public async Task WithdrawAsync_records_the_reason_as_a_note_before_the_transition()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("submitted");

        await harness.Service.WithdrawAsync(harness.WorkItem.Id, Reason, harness.User, ct);

        Received.InOrder(() =>
        {
            harness.Engine.AddNoteAsync(harness.WorkItem.Id, Reason, harness.User, ct);
            harness.Engine.ApplyActionAsync(harness.WorkItem.Id, "withdraw", harness.User, ct);
        });
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    public async Task WithdrawAsync_fails_with_invalid_transition_from_a_decided_state(
        string stateId
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId);

        var result = await harness.Service.WithdrawAsync(
            harness.WorkItem.Id,
            Reason,
            harness.User,
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        Assert.Contains(stateId, result.Message);
        await harness
            .Engine.DidNotReceiveWithAnyArgs()
            .ApplyActionAsync(default, default!, default!, default);
        await harness
            .Engine.DidNotReceiveWithAnyArgs()
            .AddNoteAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task WithdrawAsync_is_an_idempotent_replay_when_already_withdrawn()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("withdrawn");

        var result = await harness.Service.WithdrawAsync(
            harness.WorkItem.Id,
            Reason,
            harness.User,
            ct
        );

        Assert.True(result.IsSuccess);
        Assert.True(result.IsIdempotentReplay);
        await harness
            .Engine.DidNotReceiveWithAnyArgs()
            .ApplyActionAsync(default, default!, default!, default);
        await harness
            .Engine.DidNotReceiveWithAnyArgs()
            .AddNoteAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task WithdrawAsync_returns_not_found_for_a_missing_work_item()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("submitted", seedWorkItem: false);

        var result = await harness.Service.WithdrawAsync(
            harness.WorkItem.Id,
            Reason,
            harness.User,
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
    }

    [Fact]
    public async Task WithdrawAsync_returns_unknown_action_for_a_different_work_item_type()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("submitted", typeId: "some-other-type");

        var result = await harness.Service.WithdrawAsync(
            harness.WorkItem.Id,
            Reason,
            harness.User,
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.UnknownAction, result.FailureCode);
    }

    [Fact]
    public async Task WithdrawAsync_does_not_apply_the_transition_when_the_note_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("submitted");
        harness
            .Engine.AddNoteAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                WorkItemActionResult.Failure(WorkItemActionFailureCode.InvalidNote, "bad note")
            );

        var result = await harness.Service.WithdrawAsync(
            harness.WorkItem.Id,
            Reason,
            harness.User,
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidNote, result.FailureCode);
        await harness
            .Engine.DidNotReceiveWithAnyArgs()
            .ApplyActionAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task WithdrawAsync_propagates_a_transition_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("submitted");
        harness
            .Engine.ApplyActionAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                WorkItemActionResult.Failure(WorkItemActionFailureCode.ConcurrencyConflict, "raced")
            );

        var result = await harness.Service.WithdrawAsync(
            harness.WorkItem.Id,
            Reason,
            harness.User,
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.ConcurrencyConflict, result.FailureCode);
    }

    private sealed class Harness
    {
        public Harness(
            string stateId,
            bool seedWorkItem = true,
            string typeId = ReAccreditationType.Id,
            string submittedBy = TenantClientId,
            ClaimsPrincipal? user = null
        )
        {
            WorkItem = new WorkItem
            {
                TypeId = typeId,
                StateId = stateId,
                SubmittedBy = submittedBy,
            };

            Persistence = Substitute.For<IWorkItemPersistence>();
            Persistence
                .GetByIdAsync(WorkItem.Id, Arg.Any<CancellationToken>())
                .Returns(seedWorkItem ? WorkItem : null);

            Engine = Substitute.For<IWorkItemService>();
            Engine
                .AddNoteAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<ClaimsPrincipal>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(WorkItemActionResult.Success(WorkItem));
            Engine
                .ApplyActionAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<ClaimsPrincipal>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(WorkItemActionResult.Success(WorkItem));

            User =
                user
                ?? new ClaimsPrincipal(
                    new ClaimsIdentity(
                        [
                            new Claim("user:id", "alice-1"),
                            new Claim("user:name", "Alice Example"),
                            new Claim("client_id", TenantClientId),
                        ],
                        "test"
                    )
                );

            Service = new ReAccreditationWithdrawService(
                Persistence,
                Engine,
                NullLogger<ReAccreditationWithdrawService>.Instance
            );
        }

        public WorkItem WorkItem { get; }
        public IWorkItemPersistence Persistence { get; }
        public IWorkItemService Engine { get; }
        public ClaimsPrincipal User { get; }
        public ReAccreditationWithdrawService Service { get; }
    }
}
