using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.Workflows;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Workflows.StandardLifecycle;

/// <summary>
/// RA-495 review: a work item whose rules cannot be resolved (its type has
/// opted in to the Standard lifecycle but is not registered, and it has no
/// stored template snapshot) is a server-side problem, not a wrong type. It
/// fails with <see cref="WorkItemActionFailureCode.TemplateUnavailable"/>,
/// which maps to a generic 500 with no <c>errorCode</c>, and never with
/// <c>errorCode: wrong-work-item-type</c>, which stays the "type has not opted
/// in" failure.
/// </summary>
public sealed class StandardLifecycleTemplateUnavailableTests
{
    private const string TypeId = "test-standard-unregistered";

    [Fact]
    public async Task Duly_making_an_item_whose_template_cannot_be_resolved_fails_as_template_unavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = new WorkItem
        {
            TypeId = TypeId,
            StateId = "submitted",
            TemplateSnapshot = null,
            TemplateVersion = null,
        };
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.GetByIdAsync(workItem.Id, ct).Returns(workItem);
        var service = new StandardDulyMakingService(
            persistence,
            new WorkItemRegistry([]),
            [],
            TimeProvider.System,
            NullLogger.Instance,
            new StandardLifecycleStrategies<IStandardDulyMakingRules>([new UnregisteredTypeRules()])
        );

        var result = await service.CompleteDulyMakingAsync(
            workItem.Id,
            DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1),
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("user:id", "alice-1")], "test")),
            ct
        );

        Assert.Equal(WorkItemActionFailureCode.TemplateUnavailable, result.FailureCode);
        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);

        var problem = StandardLifecycleProblems.ServiceFailure(result, "Duly make failed");
        Assert.Equal(StatusCodes.Status500InternalServerError, problem.StatusCode);
        Assert.DoesNotContain(StandardLifecycleProblems.ErrorCodeKey, problem.ProblemDetails.Extensions.Keys);
    }

    [Fact]
    public void A_template_unavailable_determination_step_is_a_500_with_no_error_code()
    {
        var problem = StandardLifecycleProblems.DecisionFailure(
            WorkItemActionResult.Failure(WorkItemActionFailureCode.TemplateUnavailable, "no template"),
            "Decision failed"
        );

        Assert.Equal(StatusCodes.Status500InternalServerError, problem.StatusCode);
        Assert.DoesNotContain(StandardLifecycleProblems.ErrorCodeKey, problem.ProblemDetails.Extensions.Keys);
    }

    [Fact]
    public void A_type_that_has_not_opted_in_is_still_labelled_wrong_work_item_type()
    {
        var notOptedIn = new StandardLifecycleStrategies<IStandardDulyMakingRules>([new UnregisteredTypeRules()])
            .NotOptedIn(Guid.NewGuid(), "re-accreditation");

        var problem = StandardLifecycleProblems.ServiceFailure(notOptedIn, "Duly make failed");

        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Equal(
            StandardLifecycleProblems.WrongWorkItemTypeErrorCode,
            problem.ProblemDetails.Extensions[StandardLifecycleProblems.ErrorCodeKey]
        );
    }

    /// <summary>An opted-in type that is deliberately never registered.</summary>
    private sealed class UnregisteredTypeRules : IStandardDulyMakingRules
    {
        public IWorkItemType Type { get; } = new UnregisteredType();

        public int? ReadChargePence(WorkItem workItem) => null;
    }

    private sealed class UnregisteredType : IWorkItemType
    {
        public string TypeId => StandardLifecycleTemplateUnavailableTests.TypeId;
        public string DisplayName => "Unregistered Standard lifecycle test";
        public string TemplateVersion => "v1";
        public WorkItemWorkflow Workflow => StandardWorkflow.Definition;
        public WorkItemState InitialState => Workflow.InitialState;
        public IReadOnlyCollection<WorkItemState> States => Workflow.States;
        public IReadOnlyCollection<WorkItemTransition> Transitions => Workflow.Transitions;
    }
}
