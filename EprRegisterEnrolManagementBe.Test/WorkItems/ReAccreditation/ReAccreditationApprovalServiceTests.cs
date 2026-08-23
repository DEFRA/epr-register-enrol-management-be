using System.Security.Claims;
using EprRegisterEnrolManagementBe.Config;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Utils.Background;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-132: unit-level tests for <see cref="ReAccreditationApprovalService"/>.
/// Persistence, hooks, queue and the accreditation number adapter are all
/// substituted so each branch of the validate → mutate → audit → enqueue →
/// fan-out pipeline can be asserted in isolation.
///
/// RA-448 phase 2: the accreditation id no longer comes from a local
/// generator — it comes from <see cref="IAccreditationNumberAdapter"/>, a
/// real call to the backend. <see cref="BuildWorkItem"/>'s default payload
/// therefore carries the three fields the service needs to make that call
/// (operatorOrganisationId, operatorApplicationId, nation) alongside the
/// pre-existing organisationName/registrationNumber fields.
/// operatorApplicationId (not operatorRegistrationId — a review-confirmed
/// fix; see ReAccreditationApprovalService's comment at the adapter call
/// site) is the backend's own AccreditationApplicationModel id, forwarded
/// as the {applicationId} route segment.
/// </summary>
public class ReAccreditationApprovalServiceTests
{
    private const string DecisionMakerId = "alice-1";
    private const string OtherTenantClientId = "other-tenant";
    private const string OwnerClientId = "test-client";

    private static readonly DateTimeOffset s_fixedNow = new(2025, 02, 03, 12, 30, 0, TimeSpan.Zero);

    private static ClaimsPrincipal DecisionMaker(string? clientId = OwnerClientId) =>
        new(
            new ClaimsIdentity(
                [
                    new Claim("user:id", DecisionMakerId),
                    new Claim("user:name", "Alice Example"),
                    new Claim("client_id", clientId ?? OwnerClientId),
                    new Claim(ClaimTypes.Role, "reaccreditation-decision-maker"),
                ],
                "test"
            )
        );

    private static ClaimsPrincipal AnonymousUser() =>
        new(
            new ClaimsIdentity(
                [
                    new Claim("client_id", OwnerClientId),
                    new Claim(ClaimTypes.Role, "reaccreditation-decision-maker"),
                ],
                "test"
            )
        );

    /// <summary>Build a re-accreditation work item.</summary>
    private static WorkItem BuildWorkItem(
        string stateId = "awaiting-decision",
        string? submittedBy = OwnerClientId,
        BsonDocument? payload = null,
        string typeId = ReAccreditationType.Id
    )
    {
        var type = new ReAccreditationType();
        return new WorkItem
        {
            TypeId = typeId,
            StateId = stateId,
            SubmittedBy = submittedBy,
            Payload =
                payload
                ?? new BsonDocument
                {
                    ["organisationName"] = "Acme Ltd",
                    ["registrationNumber"] = "EX-001",
                    // RA-448 phase 2 review: operatorApplicationId (the backend's
                    // AccreditationApplicationModel.Id, confirmed against
                    // HttpCaseWorkingApiAdapter.BuildPayload), not
                    // operatorRegistrationId, is required to call
                    // IAccreditationNumberAdapter.
                    ["operatorOrganisationId"] = "500027",
                    ["operatorApplicationId"] = "APP-500027",
                    ["operatorRegistrationId"] = "reg-500027",
                    ["nation"] = "England",
                },
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
            TemplateVersion = type.TemplateVersion,
        };
    }

    private sealed record Sut(
        ReAccreditationApprovalService Service,
        IWorkItemPersistence Persistence,
        IAccreditationNumberAdapter NumberAdapter,
        IBackgroundTaskQueue Queue,
        List<IWorkItemPostActionHook> Hooks,
        FakeTimeProvider Time
    );

    private static Sut Build(
        string accreditationId = "A25ER5000270036WO",
        int currentYear = 2025,
        DateTimeOffset? now = null,
        bool registerType = true
    )
    {
        var persistence = Substitute.For<IWorkItemPersistence>();
        var numberAdapter = Substitute.For<IAccreditationNumberAdapter>();
        numberAdapter
            .GenerateOrUpdateAccreditationNumberAsync(
                Arg.Any<AccreditationNumberRequest>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(AccreditationNumberResult.Success(accreditationId)));
        var queue = Substitute.For<IBackgroundTaskQueue>();
        var hooks = new List<IWorkItemPostActionHook> { Substitute.For<IWorkItemPostActionHook>() };
        var time = new FakeTimeProvider(now ?? s_fixedNow);

        var sut = new ReAccreditationApprovalService(
            persistence,
            new WorkItemRegistry(registerType ? [new ReAccreditationType()] : []),
            numberAdapter,
            queue,
            hooks,
            NullLogger<ReAccreditationApprovalService>.Instance,
            Options.Create(new AccreditationConfig { CurrentYear = currentYear }),
            time
        );

        return new Sut(sut, persistence, numberAdapter, queue, hooks, time);
    }

    // ──────────────── RA-410: awaiting-decision task gate removed ────────────────

    /// <summary>
    /// RA-346 AC2 root cause: 'approve' is not a registered
    /// <see cref="WorkItemTransition"/>, so it bypassed the engine's
    /// task-completeness gate entirely and a caseworker could approve a
    /// determination with 'record-decision-rationale' still pending.
    ///
    /// RA-410: the task framework (and this gate) is gone, so approval no
    /// longer depends on any task state at all — regression cover for the
    /// ungating.
    /// </summary>
    [Fact]
    public async Task ApproveAsync_succeeds_from_awaiting_decision_now_the_task_gate_is_gone()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem();
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("approved", workItem.StateId);
    }

    /// <summary>
    /// RA-346: a legacy work item with no stored snapshot falls back to the
    /// live registered type, exactly as the engine does.
    /// </summary>
    [Fact]
    public async Task ApproveAsync_falls_back_to_the_registered_type_when_no_snapshot_is_stored()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem();
        workItem.TemplateSnapshot = null;
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("approved", workItem.StateId);
    }

    /// <summary>
    /// RA-346: with neither a snapshot nor a registered type there is no
    /// template to judge tasks against. Refuse rather than approve blind.
    /// </summary>
    [Fact]
    public async Task ApproveAsync_refuses_when_no_template_can_be_resolved()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build(registerType: false);
        var workItem = BuildWorkItem();
        workItem.TemplateSnapshot = null;
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.UnknownAction, result.FailureCode);
        Assert.Contains("has no stored template snapshot", result.Message);
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    // ─────────────────────────── happy path ───────────────────────────

    [Fact]
    public async Task ApproveAsync_stamps_payload_transitions_state_appends_three_audit_entries_and_fans_out()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build("A25ER5000270036WO");
        var workItem = BuildWorkItem();
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.True(result.IsSuccess);
        Assert.Equal("approved", workItem.StateId);
        Assert.Equal(s_fixedNow.UtcDateTime, workItem.LastModifiedAt);

        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(workItem.Payload);
        Assert.Equal("A25ER5000270036WO", payload.AccreditationId);
        Assert.Equal(DateOnly.FromDateTime(s_fixedNow.UtcDateTime), payload.AccreditationStartDate);
        Assert.Equal(2025, payload.AccreditationYear);
        Assert.NotNull(payload.SlaClock);
        Assert.Equal(s_fixedNow, payload.SlaClock!.StoppedAt);
        // RA-132 must not nuke existing payload fields.
        Assert.Equal("Acme Ltd", payload.OrganisationName);

        Assert.Equal(3, workItem.AuditLog.Count);
        Assert.Equal("action-applied", workItem.AuditLog[0].Action);
        Assert.Equal("approve", workItem.AuditLog[0].Details["actionId"]);
        Assert.Equal("approved", workItem.AuditLog[0].Details["toStateId"]);
        Assert.Equal("sla-clock-stopped", workItem.AuditLog[1].Action);
        Assert.Equal("accreditation-issued", workItem.AuditLog[2].Action);
        Assert.Equal("A25ER5000270036WO", workItem.AuditLog[2].Details["accreditationId"]);
        Assert.Equal("2025", workItem.AuditLog[2].Details["accreditationYear"]);
        // epr-rr9s: every entry this path writes snapshots the state as of the
        // event — the post-transition 'approved' — so the auxiliary
        // sla-clock-stopped / accreditation-issued rows are not state-less.
        Assert.Equal(
            ["approved", "approved", "approved"],
            workItem.AuditLog.Select(e => e.StateId).ToArray()
        );

        await sut.Persistence.Received(1).ReplaceAsync(workItem, Arg.Any<CancellationToken>());
        await sut
            .Queue.Received(1)
            .QueueAsync(
                Arg.Any<Func<IServiceProvider, CancellationToken, Task>>(),
                Arg.Any<CancellationToken>()
            );
        await sut.Hooks[0]
            .Received(1)
            .OnActionAppliedAsync(
                workItem,
                "approve",
                "awaiting-decision",
                Arg.Any<ClaimsPrincipal>(),
                ct
            );
    }

    [Fact]
    public async Task ApproveAsync_preserves_unmodelled_payload_keys_and_sets_approval_fields()
    {
        // RA-249: approval must MERGE the modelled updates over the existing
        // payload, not replace it. A full replace against the
        // [BsonIgnoreExtraElements] model dropped every unmodelled key
        // (applicationReference, source, siteAddress*), turning the
        // application ref into the work-item Guid downstream.
        var ct = TestContext.Current.CancellationToken;
        var sut = Build("A25ER5000270036WO");
        var workItem = BuildWorkItem(
            payload: new BsonDocument
            {
                ["organisationName"] = "Acme Ltd",
                ["registrationNumber"] = "EX-001",
                ["operatorOrganisationId"] = "500027",
                ["operatorApplicationId"] = "APP-500027",
                ["operatorRegistrationId"] = "reg-500027",
                ["nation"] = "England",
                // Unmodelled keys that the model would otherwise discard.
                ["applicationReference"] = "RA-000000123",
                ["source"] = "external-portal",
                ["siteAddressLine1"] = "1 Recycling Way",
                ["siteAddress"] = new BsonDocument
                {
                    ["line1"] = "1 Recycling Way",
                    ["postcode"] = "AB1 2CD",
                },
            }
        );
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.True(result.IsSuccess);

        // Unmodelled keys survive with their original values.
        Assert.Equal("RA-000000123", workItem.Payload["applicationReference"].AsString);
        Assert.Equal("external-portal", workItem.Payload["source"].AsString);
        Assert.Equal("1 Recycling Way", workItem.Payload["siteAddressLine1"].AsString);
        var nested = workItem.Payload["siteAddress"].AsBsonDocument;
        Assert.Equal("1 Recycling Way", nested["line1"].AsString);
        Assert.Equal("AB1 2CD", nested["postcode"].AsString);

        // Modelled keys that pre-existed are untouched.
        Assert.Equal("Acme Ltd", workItem.Payload["organisationName"].AsString);
        Assert.Equal("EX-001", workItem.Payload["registrationNumber"].AsString);

        // The four approval fields are set/overwritten on the merged payload.
        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(workItem.Payload);
        Assert.Equal("A25ER5000270036WO", payload.AccreditationId);
        Assert.Equal(DateOnly.FromDateTime(s_fixedNow.UtcDateTime), payload.AccreditationStartDate);
        Assert.Equal(2025, payload.AccreditationYear);
        Assert.NotNull(payload.SlaClock);
        Assert.Equal(s_fixedNow, payload.SlaClock!.StoppedAt);
    }

    [Fact]
    public async Task ApproveAsync_preserves_ra292_overseas_site_and_authoriser_flags()
    {
        // RA-292: the new-ORS / new-interim-site / authority-to-issue flags are
        // payload data the operator backend produces and the case management
        // frontend badges. They live two and three levels deep inside
        // `overseasSites.sites[].interimSite` and `prns.authorisers[]`, and
        // ReAccreditationPayload models neither top-level key.
        //
        // Approval is the ONLY place that round-trips the payload through that
        // [BsonIgnoreExtraElements] model, so it is the one operation that could
        // silently blank these fields. The merge is shallow: it survives today
        // precisely because `overseasSites` and `prns` are unmodelled. Adding
        // either to ReAccreditationPayload without deepening the merge would
        // drop every undeclared key nested inside it — this test is the guard
        // that turns that into a red build rather than a blank regulator page.
        var ct = TestContext.Current.CancellationToken;
        var sut = Build("A25ER5000270036WO");
        var workItem = BuildWorkItem(
            payload: new BsonDocument
            {
                ["organisationName"] = "Acme Ltd",
                ["operatorOrganisationId"] = "500027",
                ["operatorApplicationId"] = "APP-500027",
                ["operatorRegistrationId"] = "reg-500027",
                ["nation"] = "England",
                ["overseasSites"] = new BsonDocument
                {
                    ["sites"] = new BsonArray
                    {
                        new BsonDocument
                        {
                            ["siteId"] = 1,
                            ["orsId"] = "ORS-2026-0292",
                            ["isNewSite"] = true,
                            ["repatriatedLoads"] = "3",
                            ["interimSite"] = new BsonDocument
                            {
                                ["siteNumber"] = "INT-001",
                                ["isNewSite"] = true,
                                ["townOrCity"] = "Antwerp",
                            },
                        },
                        new BsonDocument
                        {
                            ["siteId"] = 2,
                            ["isNewSite"] = false,
                            ["interimSite"] = new BsonDocument { ["isNewSite"] = false },
                        },
                    },
                },
                ["prns"] = new BsonDocument
                {
                    ["authorisers"] = new BsonArray
                    {
                        new BsonDocument
                        {
                            ["fullName"] = "Grace Adeyemi",
                            ["email"] = "grace.adeyemi@example.com",
                            ["isNew"] = true,
                        },
                        new BsonDocument
                        {
                            ["fullName"] = "Martin Cole",
                            ["email"] = "martin.cole@example.com",
                            ["isNew"] = false,
                        },
                    },
                },
            }
        );
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.True(result.IsSuccess);

        var sites = workItem.Payload["overseasSites"]["sites"].AsBsonArray;
        Assert.Equal(2, sites.Count);

        var newSite = sites[0].AsBsonDocument;
        Assert.True(newSite["isNewSite"].AsBoolean);
        Assert.Equal("ORS-2026-0292", newSite["orsId"].AsString);
        Assert.Equal("3", newSite["repatriatedLoads"].AsString);
        Assert.True(newSite["interimSite"]["isNewSite"].AsBoolean);
        Assert.Equal("INT-001", newSite["interimSite"]["siteNumber"].AsString);
        Assert.Equal("Antwerp", newSite["interimSite"]["townOrCity"].AsString);

        var establishedSite = sites[1].AsBsonDocument;
        Assert.False(establishedSite["isNewSite"].AsBoolean);
        Assert.False(establishedSite["interimSite"]["isNewSite"].AsBoolean);

        var authorisers = workItem.Payload["prns"]["authorisers"].AsBsonArray;
        Assert.Equal(2, authorisers.Count);
        Assert.True(authorisers[0]["isNew"].AsBoolean);
        Assert.Equal("Grace Adeyemi", authorisers[0]["fullName"].AsString);
        Assert.False(authorisers[1]["isNew"].AsBoolean);
    }

    [Fact]
    public async Task ApproveAsync_overwrites_a_stale_modelled_approval_field_on_merge()
    {
        // RA-249: merge must OVERWRITE existing elements, so a stale
        // accreditationStartDate/year on the stored payload is replaced by
        // the freshly computed values rather than being preserved.
        var ct = TestContext.Current.CancellationToken;
        var sut = Build("A25ER5000270036WO", currentYear: 2025);
        var workItem = BuildWorkItem(
            payload: new BsonDocument
            {
                ["organisationName"] = "Acme Ltd",
                ["operatorOrganisationId"] = "500027",
                ["operatorApplicationId"] = "APP-500027",
                ["operatorRegistrationId"] = "reg-500027",
                ["nation"] = "England",
                ["applicationReference"] = "RA-000000999",
                // Stale modelled values that must be overwritten by approval.
                ["accreditationYear"] = 1999,
                ["accreditationStartDate"] = "1999-01-01",
            }
        );
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.True(result.IsSuccess);
        Assert.Equal("RA-000000999", workItem.Payload["applicationReference"].AsString);

        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(workItem.Payload);
        Assert.Equal(2025, payload.AccreditationYear);
        Assert.Equal(DateOnly.FromDateTime(s_fixedNow.UtcDateTime), payload.AccreditationStartDate);
    }

    [Fact]
    public async Task Queued_publishing_audit_runs_against_scoped_appender_with_accreditation_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build("A25CR5000270036WO");
        var workItem = BuildWorkItem();
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        Func<IServiceProvider, CancellationToken, Task>? captured = null;
        await sut.Queue.QueueAsync(
            Arg.Do<Func<IServiceProvider, CancellationToken, Task>>(j => captured = j),
            Arg.Any<CancellationToken>()
        );

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.True(result.IsSuccess);
        Assert.NotNull(captured);

        var appender = Substitute.For<IWorkItemAuditAppender>();
        var services = new ServiceCollection();
        services.AddSingleton(appender);
        await using var sp = services.BuildServiceProvider();

        await captured!(sp, ct);

        await appender
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "publishing-enqueued",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["accreditationId"] == "A25CR5000270036WO"
                ),
                Arg.Any<ClaimsPrincipal>(),
                ct
            );
    }

    // ─────────────────────────── validation paths ──────────────────────

    [Fact]
    public async Task Returns_MissingActorIdentity_when_user_has_no_user_id_claim()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();

        var result = await sut.Service.ApproveAsync(Guid.NewGuid(), AnonymousUser(), ct);

        Assert.Equal(WorkItemActionFailureCode.MissingActorIdentity, result.FailureCode);
        await sut.Persistence.DidNotReceiveWithAnyArgs().GetByIdAsync(default, ct);
    }

    [Fact]
    public async Task Returns_WorkItemNotFound_when_persistence_returns_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        sut.Persistence.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null);

        var result = await sut.Service.ApproveAsync(Guid.NewGuid(), DecisionMaker(), ct);

        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
    }

    [Fact]
    public async Task Succeeds_for_work_item_not_submitted_by_caller()
    {
        // RBAC (who may act on whose items) lives in the frontend now; the
        // service applies the action regardless of who submitted the item.
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem(submittedBy: OtherTenantClientId);
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Returns_UnknownAction_when_work_item_is_wrong_type()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem(typeId: "some-other-type");
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.Equal(WorkItemActionFailureCode.UnknownAction, result.FailureCode);
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("withdrawn")]
    public async Task Returns_TerminalState_for_already_terminal_work_item(string stateId)
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem(stateId: stateId);
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.Equal(WorkItemActionFailureCode.TerminalState, result.FailureCode);
    }

    [Theory]
    [InlineData("submitted")]
    [InlineData("duly-made")]
    [InlineData("assessment-in-progress")]
    public async Task Returns_InvalidTransition_for_non_awaiting_decision_states(string stateId)
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem(stateId: stateId);
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
    }

    [Fact]
    public async Task Returns_InvalidTransition_and_does_not_persist_when_existing_payload_is_corrupt()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build("A25AA5000270036WO");
        // Force the deserialiser to throw by stuffing a malformed value
        // into a typed field.
        var workItem = BuildWorkItem(
            payload: new BsonDocument { ["accreditationStartDate"] = "not-a-date" }
        );
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        // A corrupt payload must NOT silently wipe existing data — the service
        // must abort and return a failure so the operator can investigate.
        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        // Persistence must not have been called — the corrupt item is left unchanged.
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
        // The original payload should be untouched.
        Assert.Equal("not-a-date", workItem.Payload["accreditationStartDate"].AsString);
    }

    [Fact]
    public async Task Logs_and_continues_when_a_post_action_hook_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem();
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);
        sut.Hooks[0]
            .OnActionAppliedAsync(
                Arg.Any<WorkItem>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(_ => throw new InvalidOperationException("hook boom"));

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.True(result.IsSuccess);
    }

    // ─────────────────────────── concurrency retry ─────────────────────

    [Fact]
    public async Task Retries_on_concurrency_conflict_and_succeeds_within_max_attempts()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        // Hand back a fresh work-item per call so each retry sees a
        // clean assessment-in-progress doc (the production load would).
        sut.Persistence.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => BuildWorkItem());

        var calls = 0;
        sut.Persistence.When(p => p.ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                calls++;
                if (calls == 1)
                {
                    var item = call.Arg<WorkItem>();
                    throw new WorkItemConcurrencyException(item.Id, expectedVersion: 0);
                }
            });

        var result = await sut.Service.ApproveAsync(Guid.NewGuid(), DecisionMaker(), ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, calls);
    }

    /// <summary>
    /// RA-448 phase 2: the accreditation number adapter call is a real,
    /// effectful backend request, unlike the old local generator — it must
    /// be called at most once per ApproveAsync call, even when a Mongo
    /// concurrency conflict forces a retry of the persistence step.
    /// Otherwise a retry would ask the backend to "reapply" (increment YY
    /// on) a number it had only just issued a moment earlier on the first
    /// attempt, corrupting a real backend number for no reason.
    /// </summary>
    [Fact]
    public async Task Calls_the_number_adapter_at_most_once_across_concurrency_retries()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        sut.Persistence.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => BuildWorkItem());

        var calls = 0;
        sut.Persistence.When(p => p.ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                calls++;
                if (calls == 1)
                {
                    var item = call.Arg<WorkItem>();
                    throw new WorkItemConcurrencyException(item.Id, expectedVersion: 0);
                }
            });

        var result = await sut.Service.ApproveAsync(Guid.NewGuid(), DecisionMaker(), ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, calls);
        await sut
            .NumberAdapter.Received(1)
            .GenerateOrUpdateAccreditationNumberAsync(
                Arg.Any<AccreditationNumberRequest>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Returns_ConcurrencyConflict_after_three_failed_attempts()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        sut.Persistence.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => BuildWorkItem());
        sut.Persistence.When(p => p.ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                var item = call.Arg<WorkItem>();
                throw new WorkItemConcurrencyException(item.Id, expectedVersion: 0);
            });

        var result = await sut.Service.ApproveAsync(Guid.NewGuid(), DecisionMaker(), ct);

        Assert.Equal(WorkItemActionFailureCode.ConcurrencyConflict, result.FailureCode);
        await sut
            .Persistence.Received(3)
            .ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApproveAsync_throws_when_user_is_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            sut.Service.ApproveAsync(Guid.NewGuid(), user: null!, ct)
        );
    }

    // ─────────────────────────── RA-133 / RA-448 phase 2 ────────────────

    [Fact]
    public async Task ApproveAsync_resolves_the_number_from_organisation_application_nation_and_year()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build(currentYear: 2028);
        var workItem = BuildWorkItem(
            payload: new BsonDocument
            {
                ["organisationName"] = "Acme Ltd",
                ["operatorOrganisationId"] = "500099",
                ["operatorApplicationId"] = "APP-500099",
                ["nation"] = "Scotland",
            }
        );
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        await sut
            .NumberAdapter.Received(1)
            .GenerateOrUpdateAccreditationNumberAsync(
                Arg.Is<AccreditationNumberRequest>(r =>
                    r.OrganisationId == "500099"
                    && r.ApplicationId == "APP-500099"
                    && r.Nation == Nation.Scotland
                    && r.OrgId == 500099
                    && r.Year == 2028
                    // RA-448 phase 2 review: false, not true — a retried approval
                    // must idempotently return the already-issued number rather
                    // than asking the backend to bump it.
                    && !r.Regenerate
                ),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task ApproveAsync_returns_InvalidTransition_and_does_not_call_the_adapter_when_nation_is_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem(
            payload: new BsonDocument
            {
                ["organisationName"] = "Acme Ltd",
                ["operatorOrganisationId"] = "500027",
                ["operatorApplicationId"] = "APP-500027",
                ["operatorRegistrationId"] = "reg-500027",
                // nation deliberately absent
            }
        );
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
        await sut
            .NumberAdapter.DidNotReceiveWithAnyArgs()
            .GenerateOrUpdateAccreditationNumberAsync(default!, ct);
    }

    [Fact]
    public async Task ApproveAsync_returns_InvalidTransition_and_does_not_call_the_adapter_when_org_id_is_not_numeric()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem(
            payload: new BsonDocument
            {
                ["organisationName"] = "Acme Ltd",
                ["operatorOrganisationId"] = "not-a-number",
                ["operatorApplicationId"] = "APP-500027",
                ["nation"] = "England",
            }
        );
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
        await sut
            .NumberAdapter.DidNotReceiveWithAnyArgs()
            .GenerateOrUpdateAccreditationNumberAsync(default!, ct);
    }

    /// <summary>
    /// RA-249's original concern (a null stored Payload must not crash the
    /// service) still holds, but the outcome changes under RA-448 phase 2:
    /// a null payload has none of the fields required to request a real
    /// accreditation number, so approval now fails cleanly rather than
    /// (as before RA-448) succeeding with a locally-fabricated id.
    /// </summary>
    [Fact]
    public async Task ApproveAsync_fails_cleanly_without_throwing_when_stored_payload_is_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem();
        workItem.Payload = null!;
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
        await sut
            .NumberAdapter.DidNotReceiveWithAnyArgs()
            .GenerateOrUpdateAccreditationNumberAsync(default!, ct);
    }

    [Fact]
    public async Task ApproveAsync_uses_today_as_start_date_when_approval_is_after_jan_1_of_configured_year()
    {
        var ct = TestContext.Current.CancellationToken;
        // s_fixedNow = 2025-02-03; configured year 2025 → today > Jan 1.
        var sut = Build(currentYear: 2025);
        var workItem = BuildWorkItem();
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(workItem.Payload);
        Assert.Equal(new DateOnly(2025, 2, 3), payload.AccreditationStartDate);
        Assert.Equal(2025, payload.AccreditationYear);
    }

    [Fact]
    public async Task ApproveAsync_uses_jan_1_as_start_date_when_approval_is_before_jan_1_of_configured_year()
    {
        var ct = TestContext.Current.CancellationToken;
        // s_fixedNow = 2025-02-03; configured year 2027 → Jan 1 of 2027 > today.
        var sut = Build(currentYear: 2027);
        var workItem = BuildWorkItem();
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(workItem.Payload);
        Assert.Equal(new DateOnly(2027, 1, 1), payload.AccreditationStartDate);
        Assert.Equal(2027, payload.AccreditationYear);
    }

    [Fact]
    public async Task ApproveAsync_uses_jan_1_as_start_date_when_approval_is_exactly_on_jan_1_of_configured_year()
    {
        var ct = TestContext.Current.CancellationToken;
        var jan1 = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var sut = Build(currentYear: 2027, now: jan1);
        var workItem = BuildWorkItem();
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(workItem.Payload);
        Assert.Equal(new DateOnly(2027, 1, 1), payload.AccreditationStartDate);
    }

    [Fact]
    public async Task ApproveAsync_is_idempotent_when_work_item_already_carries_an_accreditation_id_and_is_approved()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem(
            stateId: "approved",
            payload: new BsonDocument
            {
                ["organisationName"] = "Acme Ltd",
                ["accreditationId"] = "A25ER5000270036WO",
            }
        );
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.True(result.IsSuccess);
        // No re-stamping, no audit entries, no persistence, no fan-out.
        Assert.Empty(workItem.AuditLog);
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
        await sut
            .NumberAdapter.DidNotReceiveWithAnyArgs()
            .GenerateOrUpdateAccreditationNumberAsync(default!, ct);
        await sut.Queue.DidNotReceiveWithAnyArgs().QueueAsync(default!, ct);
        await sut.Hooks[0]
            .DidNotReceiveWithAnyArgs()
            .OnActionAppliedAsync(default!, default!, default!, default!, ct);
    }

    /// <summary>
    /// RA-448 phase 2: idempotent-success on an already-approved item holds
    /// regardless of the stored id's format — this phase deliberately does
    /// not retroactively fix already-approved records carrying the retired
    /// local generator's shape (Phase 2 doc AC12).
    /// </summary>
    [Fact]
    public async Task ApproveAsync_is_idempotent_for_an_approved_item_even_with_a_non_standard_format_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem(
            stateId: "approved",
            payload: new BsonDocument
            {
                ["organisationName"] = "Acme Ltd",
                ["accreditationId"] = "ACC-2025-A-DEADBEEF",
            }
        );
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.True(result.IsSuccess);
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
        await sut
            .NumberAdapter.DidNotReceiveWithAnyArgs()
            .GenerateOrUpdateAccreditationNumberAsync(default!, ct);
    }

    [Fact]
    public async Task ApproveAsync_returns_InvalidTransition_when_a_wellformed_accreditation_id_is_present_but_state_is_not_approved()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem(
            stateId: "awaiting-decision",
            payload: new BsonDocument
            {
                ["organisationName"] = "Acme Ltd",
                ["operatorOrganisationId"] = "500027",
                ["operatorApplicationId"] = "APP-500027",
                ["operatorRegistrationId"] = "reg-500027",
                ["nation"] = "England",
                ["accreditationId"] = "A25ER5000270036WO",
            }
        );
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        Assert.Contains("already carries accreditation id", result.Message);
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
        await sut
            .NumberAdapter.DidNotReceiveWithAnyArgs()
            .GenerateOrUpdateAccreditationNumberAsync(default!, ct);
    }

    /// <summary>
    /// RA-448 phase 2's new requirement: a present accreditation id matching
    /// the retired local generator's exact known shape (fixed-width, 16
    /// characters) is treated as unset on a not-yet-approved item, so a real
    /// number is (re)issued via the backend rather than the legacy value
    /// blocking approval or being carried forward unchanged.
    /// </summary>
    [Fact]
    public async Task ApproveAsync_treats_a_known_legacy_format_existing_id_as_unset_and_issues_a_real_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build("A25ER5000270099WO");
        var workItem = BuildWorkItem(
            stateId: "awaiting-decision",
            payload: new BsonDocument
            {
                ["organisationName"] = "Acme Ltd",
                ["operatorOrganisationId"] = "500027",
                ["operatorApplicationId"] = "APP-500027",
                ["operatorRegistrationId"] = "reg-500027",
                ["nation"] = "England",
                // Retired AccreditationIdGenerator's exact 16-char fixed width.
                ["accreditationId"] = "A25ER00000000000",
            }
        );
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.True(result.IsSuccess, result.Message);
        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(workItem.Payload);
        Assert.Equal("A25ER5000270099WO", payload.AccreditationId);
        await sut
            .NumberAdapter.Received(1)
            .GenerateOrUpdateAccreditationNumberAsync(
                Arg.Is<AccreditationNumberRequest>(r =>
                    r.OrganisationId == "500027"
                    && r.ApplicationId == "APP-500027"
                    && r.Nation == Nation.England
                    && r.OrgId == 500027
                    && !r.Regenerate
                ),
                Arg.Any<CancellationToken>()
            );
    }

    /// <summary>
    /// RA-448 phase 2: the adapter never throws — a failed backend call is
    /// reported as a non-success AccreditationNumberResult. Approval must
    /// abandon cleanly (AccreditationNumberUnavailable, mapped to a 500 by
    /// the endpoint) rather than proceed with no number or fall back to any
    /// local generation.
    /// </summary>
    [Fact]
    public async Task ApproveAsync_returns_AccreditationNumberUnavailable_when_the_adapter_reports_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        sut.NumberAdapter.GenerateOrUpdateAccreditationNumberAsync(
                Arg.Any<AccreditationNumberRequest>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(AccreditationNumberResult.Failure("backend returned 503")));
        var workItem = BuildWorkItem();
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.Equal(WorkItemActionFailureCode.AccreditationNumberUnavailable, result.FailureCode);
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    // ─────────────────────────── review follow-ups ─────────────────────

    /// <summary>
    /// RA-448 phase 2 review: the concurrency-exhaustion path (every attempt
    /// loses the race) is the higher-risk case for the "call the adapter at
    /// most once" invariant — confirms it holds even when persistence never
    /// succeeds, not just on the happy-path retry covered above.
    /// </summary>
    [Fact]
    public async Task Calls_the_number_adapter_at_most_once_even_when_every_concurrency_retry_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        sut.Persistence.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => BuildWorkItem());
        sut.Persistence.When(p => p.ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                var item = call.Arg<WorkItem>();
                throw new WorkItemConcurrencyException(item.Id, expectedVersion: 0);
            });

        var result = await sut.Service.ApproveAsync(Guid.NewGuid(), DecisionMaker(), ct);

        Assert.Equal(WorkItemActionFailureCode.ConcurrencyConflict, result.FailureCode);
        await sut
            .NumberAdapter.Received(1)
            .GenerateOrUpdateAccreditationNumberAsync(
                Arg.Any<AccreditationNumberRequest>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task ApproveAsync_returns_InvalidTransition_and_does_not_call_the_adapter_when_org_id_is_blank()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem(
            payload: new BsonDocument
            {
                ["organisationName"] = "Acme Ltd",
                ["operatorOrganisationId"] = "",
                ["operatorApplicationId"] = "APP-500027",
                ["nation"] = "England",
            }
        );
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
        await sut
            .NumberAdapter.DidNotReceiveWithAnyArgs()
            .GenerateOrUpdateAccreditationNumberAsync(default!, ct);
    }

    [Fact]
    public async Task ApproveAsync_returns_InvalidTransition_and_does_not_call_the_adapter_when_application_id_is_blank()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem(
            payload: new BsonDocument
            {
                ["organisationName"] = "Acme Ltd",
                ["operatorOrganisationId"] = "500027",
                ["operatorApplicationId"] = "",
                ["nation"] = "England",
            }
        );
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
        await sut
            .NumberAdapter.DidNotReceiveWithAnyArgs()
            .GenerateOrUpdateAccreditationNumberAsync(default!, ct);
    }
}
