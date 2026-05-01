using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Endpoints;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

public class ReAccreditationEndpointTests
{
    private readonly IWorkItemPersistence _persistence = Substitute.For<IWorkItemPersistence>();
    private readonly IReAccreditationDecisionService _decisionService =
        Substitute.For<IReAccreditationDecisionService>();

    [Fact]
    public async Task Recommendation_returns_not_found_when_work_item_missing()
    {
        var id = Guid.NewGuid();
        _persistence.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var result = await ReAccreditationEndpoints.GetRecommendation(
            id, UserContext(), _persistence, _decisionService, TestContext.Current.CancellationToken);

        Assert.IsType<NotFound>(result.Result);
        _decisionService.DidNotReceiveWithAnyArgs().EvaluateRecommendation(default!);
    }

    [Fact]
    public async Task Recommendation_returns_problem_when_work_item_is_wrong_type()
    {
        var id = Guid.NewGuid();
        _persistence.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(new WorkItem
        {
            Id = id,
            TypeId = "some-other-type",
            StateId = "submitted",
            SubmittedBy = "test-client"
        });

        var result = await ReAccreditationEndpoints.GetRecommendation(
            id, UserContext(), _persistence, _decisionService, TestContext.Current.CancellationToken);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
    }

    [Fact]
    public async Task Recommendation_deserialises_payload_and_returns_decision_service_result()
    {
        var id = Guid.NewGuid();
        var payload = new BsonDocument
        {
            ["organisationName"] = "Acme Recycling Ltd",
            ["registrationNumber"] = "EX-12345",
            ["materialsHandled"] = new BsonArray(["plastic", "paper"]),
            ["previousAccreditationYear"] = 2024,
            ["complianceIssuesReported"] = 1
        };
        _persistence.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(new WorkItem
        {
            Id = id,
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            SubmittedBy = "test-client",
            Payload = payload
        });

        ReAccreditationPayload? capturedPayload = null;
        _decisionService
            .EvaluateRecommendation(Arg.Do<ReAccreditationPayload>(p => capturedPayload = p))
            .Returns(new ReAccreditationRecommendation(
                ReAccreditationRecommendation.Approve, "Looks good"));

        var result = await ReAccreditationEndpoints.GetRecommendation(
            id, UserContext(), _persistence, _decisionService, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<Ok<ReAccreditationRecommendationResponse>>(result.Result);
        Assert.NotNull(ok.Value);
        Assert.Equal(ReAccreditationRecommendation.Approve, ok.Value!.Recommendation);
        Assert.Equal("Looks good", ok.Value.Rationale);

        Assert.NotNull(capturedPayload);
        Assert.Equal("Acme Recycling Ltd", capturedPayload!.OrganisationName);
        Assert.Equal("EX-12345", capturedPayload.RegistrationNumber);
        Assert.Equal(new[] { "plastic", "paper" }, capturedPayload.MaterialsHandled);
        Assert.Equal(2024, capturedPayload.PreviousAccreditationYear);
        Assert.Equal(1, capturedPayload.ComplianceIssuesReported);
    }

    [Fact]
    public async Task Recommendation_passes_empty_payload_when_work_item_payload_is_empty()
    {
        // Submitting a work item without a payload is allowed by the framework.
        // The decision service still gets called (and will recommend
        // more-info-needed) so the endpoint never silently swallows the call.
        var id = Guid.NewGuid();
        _persistence.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(new WorkItem
        {
            Id = id,
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            SubmittedBy = "test-client",
            Payload = new BsonDocument()
        });
        _decisionService
            .EvaluateRecommendation(Arg.Any<ReAccreditationPayload>())
            .Returns(new ReAccreditationRecommendation(
                ReAccreditationRecommendation.MoreInfoNeeded, "Missing fields"));

        var result = await ReAccreditationEndpoints.GetRecommendation(
            id, UserContext(), _persistence, _decisionService, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<Ok<ReAccreditationRecommendationResponse>>(result.Result);
        Assert.Equal(ReAccreditationRecommendation.MoreInfoNeeded, ok.Value!.Recommendation);
        _decisionService.Received(1).EvaluateRecommendation(Arg.Any<ReAccreditationPayload>());
    }

    // -------------------- RecordDecisionRationale (atomicity) --------------------
    //
    // The endpoint used to call AddNoteAsync followed by CompleteTaskAsync;
    // a failure of the second call left the note persisted against an
    // unfinished task. The fix is the framework's atomic
    // AddNoteAndCompleteTaskAsync — these tests exercise the real
    // WorkItemService end-to-end with a substituted IWorkItemPersistence
    // (the test project does not actually ship Ephemeral MongoDB; we
    // approximate "real Mongo" by using the real engine and asserting on
    // the document handed to ReplaceAsync, plus on the failure path that
    // ReplaceAsync was attempted at most once).

    private static WorkItem ExistingAwaitingDecisionWorkItem(Guid id)
    {
        var type = new ReAccreditationType();
        return new WorkItem
        {
            Id = id,
            TypeId = ReAccreditationType.Id,
            StateId = "awaiting-decision",
            SubmittedBy = "test-client",
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
            TemplateVersion = type.TemplateVersion
        };
    }

    private static WorkItemService BuildEngine(IWorkItemPersistence persistence) =>
        new(
            new WorkItemRegistry([new ReAccreditationType()]),
            persistence,
            NullLogger<WorkItemService>.Instance);

    private static HttpContext UserContext(
        string userId = "alice-1",
        string userName = "Alice Example",
        bool decisionMaker = true)
    {
        var ctx = new DefaultHttpContext();
        var claims = new List<Claim>
        {
            new("cognito:client_id", "test-client"),
            new("user:id", userId),
            new("user:name", userName)
        };
        // RecordDecisionRationale enforces DecisionMakerRole (epr-jdv);
        // tests that exercise success / cross-tenant / concurrency paths
        // need the role, so make it the default and let the dedicated
        // 403 test opt out via decisionMaker:false.
        if (decisionMaker)
        {
            claims.Add(new Claim(ClaimTypes.Role, ReAccreditationType.DecisionMakerRole));
        }
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        return ctx;
    }

    [Fact]
    public async Task RecordDecisionRationale_persists_note_and_completion_in_a_single_write()
    {
        var id = Guid.NewGuid();
        var stored = ExistingAwaitingDecisionWorkItem(id);
        var initialVersion = stored.Version;
        _persistence.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(stored);
        // Mirror real WorkItemPersistence.ReplaceAsync's optimistic-concurrency
        // version bump so the "Version incremented by exactly 1" assertion
        // exercises the framework contract end-to-end (the substitute would
        // otherwise leave Version untouched).
        _persistence
            .When(p => p.ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()))
            .Do(call => ((WorkItem)call.Args()[0]!).Version++);

        var result = await ReAccreditationEndpoints.RecordDecisionRationale(
            id,
            new DecisionRationaleRequest("Approved on the basis of full compliance history."),
            UserContext(),
            _persistence,
            BuildEngine(_persistence),
            TestContext.Current.CancellationToken);

        var ok = Assert.IsType<Ok<WorkItemResponse>>(result.Result);
        Assert.NotNull(ok.Value);

        // Atomicity: exactly one ReplaceAsync, with both mutations on the
        // document handed to persistence.
        await _persistence.Received(1).ReplaceAsync(stored, Arg.Any<CancellationToken>());
        var note = Assert.Single(stored.Notes);
        Assert.StartsWith("[decision-rationale] ", note.Text);
        Assert.Contains("record-decision-rationale",
            stored.CompletedTaskIdsByState["awaiting-decision"]);

        // Version bumped by exactly one — the WorkItemPersistence contract.
        Assert.Equal(initialVersion + 1, stored.Version);

        // Two audit entries — note + completion — both attributed to alice.
        Assert.Equal(2, stored.AuditLog.Count);
        Assert.Contains(stored.AuditLog, a => a.Action == "note-added"
            && a.CreatedBy == "alice-1");
        Assert.Contains(stored.AuditLog, a => a.Action == "task-completed"
            && a.Details.GetValueOrDefault("taskId") == "record-decision-rationale");
    }

    [Fact]
    public async Task RecordDecisionRationale_concurrency_conflict_persists_neither_half()
    {
        var id = Guid.NewGuid();
        var stored = ExistingAwaitingDecisionWorkItem(id);
        var initialVersion = stored.Version;
        _persistence.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(stored);

        // Simulate a concurrent writer winning the race: ReplaceAsync rolls
        // its in-memory version increment back and throws. This is the
        // scenario the bug report is about — under the old two-call
        // implementation, AddNoteAsync had already committed before this
        // throw happened.
        _persistence
            .When(p => p.ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()))
            .Do(_ =>
            {
                stored.Version = initialVersion;
                throw new WorkItemConcurrencyException(id, initialVersion);
            });

        var result = await ReAccreditationEndpoints.RecordDecisionRationale(
            id,
            new DecisionRationaleRequest("Approved on the basis of full compliance history."),
            UserContext(),
            _persistence,
            BuildEngine(_persistence),
            TestContext.Current.CancellationToken);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);

        // Exactly one write was attempted (atomic) — and it failed, so the
        // persistent version is unchanged. The on-disk document has neither
        // the note nor the completion: the engine's mutations were
        // in-memory only.
        await _persistence.Received(1).ReplaceAsync(stored, Arg.Any<CancellationToken>());
        Assert.Equal(initialVersion, stored.Version);
    }

    [Fact]
    public async Task RecordDecisionRationale_short_rationale_is_rejected_before_any_engine_call()
    {
        var id = Guid.NewGuid();

        var result = await ReAccreditationEndpoints.RecordDecisionRationale(
            id,
            new DecisionRationaleRequest("nope"),
            UserContext(),
            _persistence,
            BuildEngine(_persistence),
            TestContext.Current.CancellationToken);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        await _persistence.DidNotReceiveWithAnyArgs().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordDecisionRationale_returns_not_found_for_missing_work_item()
    {
        var id = Guid.NewGuid();
        _persistence.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var result = await ReAccreditationEndpoints.RecordDecisionRationale(
            id,
            new DecisionRationaleRequest("Approved on the basis of full compliance history."),
            UserContext(),
            _persistence,
            BuildEngine(_persistence),
            TestContext.Current.CancellationToken);

        Assert.IsType<NotFound>(result.Result);
        await _persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordDecisionRationale_rejects_wrong_work_item_type()
    {
        var id = Guid.NewGuid();
        _persistence.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(new WorkItem
        {
            Id = id,
            TypeId = "some-other-type",
            StateId = "submitted",
            SubmittedBy = "test-client"
        });

        var result = await ReAccreditationEndpoints.RecordDecisionRationale(
            id,
            new DecisionRationaleRequest("Approved on the basis of full compliance history."),
            UserContext(),
            _persistence,
            BuildEngine(_persistence),
            TestContext.Current.CancellationToken);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        await _persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    // -------------------- Cross-tenant gating (epr-946) --------------------
    //
    // ReAccreditation endpoints used to call persistence.GetByIdAsync
    // without any role / SubmittedBy check, so any authenticated caller
    // could read the recommendation for, or record a decision rationale
    // against, any work item by id. Both endpoints now route through
    // WorkItemTenancy.CanRead — these tests pin that contract.

    private static HttpContext CaseWorkerContext(bool decisionMaker = true)
    {
        var ctx = new DefaultHttpContext();
        var claims = new List<Claim>
        {
            new("cognito:client_id", "case-worker-client"),
            new("user:id", "worker-1"),
            new(ClaimTypes.Role, WorkItemEndpoints.CaseWorkerRole)
        };
        // The cross-tenant test for RecordDecisionRationale also needs
        // DecisionMakerRole (epr-jdv): tenancy and segregation-of-duties
        // are orthogonal gates and the endpoint enforces both.
        if (decisionMaker)
        {
            claims.Add(new Claim(ClaimTypes.Role, ReAccreditationType.DecisionMakerRole));
        }
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        return ctx;
    }

    [Fact]
    public async Task Recommendation_returns_not_found_for_cross_tenant_caller()
    {
        var id = Guid.NewGuid();
        _persistence.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(new WorkItem
        {
            Id = id,
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            // Owned by someone else; UserContext()'s cognito client id is
            // 'test-client' so the gate must fire.
            SubmittedBy = "other-tenant"
        });

        var result = await ReAccreditationEndpoints.GetRecommendation(
            id, UserContext(), _persistence, _decisionService, TestContext.Current.CancellationToken);

        Assert.IsType<NotFound>(result.Result);
        _decisionService.DidNotReceiveWithAnyArgs().EvaluateRecommendation(default!);
    }

    [Fact]
    public async Task Recommendation_allows_case_worker_to_see_other_tenants_item()
    {
        var id = Guid.NewGuid();
        _persistence.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(new WorkItem
        {
            Id = id,
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            SubmittedBy = "other-tenant",
            Payload = new BsonDocument()
        });
        _decisionService
            .EvaluateRecommendation(Arg.Any<ReAccreditationPayload>())
            .Returns(new ReAccreditationRecommendation(
                ReAccreditationRecommendation.Approve, "ok"));

        var result = await ReAccreditationEndpoints.GetRecommendation(
            id, CaseWorkerContext(), _persistence, _decisionService, TestContext.Current.CancellationToken);

        Assert.IsType<Ok<ReAccreditationRecommendationResponse>>(result.Result);
    }

    [Fact]
    public async Task RecordDecisionRationale_returns_not_found_for_cross_tenant_caller()
    {
        var id = Guid.NewGuid();
        _persistence.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(new WorkItem
        {
            Id = id,
            TypeId = ReAccreditationType.Id,
            StateId = "awaiting-decision",
            SubmittedBy = "other-tenant"
        });

        var result = await ReAccreditationEndpoints.RecordDecisionRationale(
            id,
            new DecisionRationaleRequest("Approved on the basis of full compliance history."),
            UserContext(),
            _persistence,
            BuildEngine(_persistence),
            TestContext.Current.CancellationToken);

        Assert.IsType<NotFound>(result.Result);
        // No write \u2014 a hand-crafted POST against another tenant's item
        // must not append the note or complete the rationale task.
        await _persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordDecisionRationale_allows_case_worker_against_other_tenants_item()
    {
        var id = Guid.NewGuid();
        var type = new ReAccreditationType();
        // Built fresh here (rather than via ExistingAwaitingDecisionWorkItem)
        // because SubmittedBy is init-only and the case-worker scenario
        // needs a different value than the helper's "test-client" default.
        var stored = new WorkItem
        {
            Id = id,
            TypeId = ReAccreditationType.Id,
            StateId = "awaiting-decision",
            SubmittedBy = "other-tenant",
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
            TemplateVersion = type.TemplateVersion
        };
        _persistence.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(stored);
        _persistence
            .When(p => p.ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()))
            .Do(call => ((WorkItem)call.Args()[0]!).Version++);

        var result = await ReAccreditationEndpoints.RecordDecisionRationale(
            id,
            new DecisionRationaleRequest("Approved on the basis of full compliance history."),
            CaseWorkerContext(),
            _persistence,
            BuildEngine(_persistence),
            TestContext.Current.CancellationToken);

        Assert.IsType<Ok<WorkItemResponse>>(result.Result);
        await _persistence.Received(1).ReplaceAsync(stored, Arg.Any<CancellationToken>());
    }

    // -------------------- Segregation of duties (epr-jdv) --------------------
    //
    // ReAccreditationType documents that an assessor who completes tasks
    // must not also record the final decision. The framework enforces this
    // for the approve/reject transitions via WorkItemTransition.RequiredRoles;
    // RecordDecisionRationale enforces the same role at the endpoint
    // because it both completes the prerequisite task and writes the
    // justification note that the decision is built upon. A non-DecisionMaker
    // hand-crafting this POST must be denied with 403 ProblemDetails before
    // any persistence happens.

    [Fact]
    public async Task RecordDecisionRationale_returns_forbidden_for_non_decision_maker()
    {
        var id = Guid.NewGuid();

        var result = await ReAccreditationEndpoints.RecordDecisionRationale(
            id,
            new DecisionRationaleRequest("Approved on the basis of full compliance history."),
            UserContext(decisionMaker: false),
            _persistence,
            BuildEngine(_persistence),
            TestContext.Current.CancellationToken);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, problem.StatusCode);
        // Fail-closed before any I/O: persistence must not be touched.
        await _persistence.DidNotReceiveWithAnyArgs().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordDecisionRationale_case_worker_without_decision_maker_role_is_forbidden()
    {
        // Cross-tenant access (CaseWorkerRole) does not bypass segregation
        // of duties — a case-worker who is not also a DecisionMaker must
        // still be denied.
        var id = Guid.NewGuid();

        var result = await ReAccreditationEndpoints.RecordDecisionRationale(
            id,
            new DecisionRationaleRequest("Approved on the basis of full compliance history."),
            CaseWorkerContext(decisionMaker: false),
            _persistence,
            BuildEngine(_persistence),
            TestContext.Current.CancellationToken);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, problem.StatusCode);
        await _persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }
}