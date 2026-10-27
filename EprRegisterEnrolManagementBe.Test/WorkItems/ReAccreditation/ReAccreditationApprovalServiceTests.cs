using System.Security.Claims;
using EprRegisterEnrolManagementBe.Config;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.Utils.Background;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        bool registerType = true,
        ILogger<ReAccreditationApprovalService>? logger = null
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
            logger ?? NullLogger<ReAccreditationApprovalService>.Instance,
            Options.Create(new AccreditationConfig { CurrentYear = currentYear }),
            time
        );

        return new Sut(sut, persistence, numberAdapter, queue, hooks, time);
    }

    // ──────────────── RA-527: step 2 — ApproveAsync (confirm applicant contact) ────────────────

    private const string StepOneNumber = "A25ER5000270036WO";

    private static readonly DateTimeOffset s_appliedAt = new(2025, 03, 04, 09, 15, 0, TimeSpan.Zero);

    /// <summary>
    /// A work item as step 1 leaves it: in approved-pending-contact, carrying
    /// only the minted number (RA-527 task 007: the start date and year are
    /// stamped at step 2).
    /// </summary>
    private static WorkItem BuildPendingContact(BsonDocument? payload = null)
    {
        var workItem = BuildWorkItem(
            stateId: "approved-pending-contact",
            payload:
                payload
                ?? new BsonDocument
                {
                    ["organisationName"] = "Acme Ltd",
                    ["registrationNumber"] = "EX-001",
                    ["operatorOrganisationId"] = "500027",
                    ["operatorApplicationId"] = "APP-500027",
                    ["nation"] = "England",
                    ["accreditationId"] = StepOneNumber,
                }
        );
        return workItem;
    }

    private static BsonDocument PendingPayloadWith(params (string Key, BsonValue Value)[] extra)
    {
        var payload = BuildPendingContact().Payload;
        foreach (var (key, value) in extra)
        {
            payload[key] = value;
        }
        return payload;
    }

    /// <summary>
    /// RA-527 step 2: one write applies the approval. The SLA clock stops at
    /// the caller's timestamp (the same one pushed to the operator journey),
    /// the item moves to Granted, and the action-applied, "Applicant
    /// contacted", sla-clock-stopped and accreditation-issued entries are
    /// written. The step 1 number is reused, never re-minted; the start date
    /// (from this step's instant) and year are stamped here (RA-527 task
    /// 007). Publishing is queued and the hooks (the Decision email) run with
    /// the confirm-contact action.
    /// </summary>
    [Fact]
    public async Task ApproveAsync_applies_the_approval_from_approved_pending_contact()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build("A25ER9999999999WO");
        var workItem = BuildPendingContact();
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), s_appliedAt, ct);

        Assert.True(result.IsSuccess, result.Message);
        Assert.False(result.IsIdempotentReplay);
        Assert.Equal("approved", workItem.StateId);
        Assert.Equal(s_appliedAt.UtcDateTime, workItem.LastModifiedAt);

        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(workItem.Payload);
        Assert.Equal(s_appliedAt, payload.SlaClock?.StoppedAt);
        Assert.Equal(StepOneNumber, payload.AccreditationId);
        Assert.Equal(new DateOnly(2025, 3, 4), payload.AccreditationStartDate);
        Assert.Equal(2025, payload.AccreditationYear);
        Assert.Equal("Acme Ltd", payload.OrganisationName);

        Assert.Collection(
            workItem.AuditLog,
            applied =>
            {
                Assert.Equal("action-applied", applied.Action);
                Assert.Equal("confirm-contact-approved", applied.Details["actionId"]);
                Assert.Equal("Confirm applicant contact", applied.Details["actionDisplayName"]);
                Assert.Equal("approved-pending-contact", applied.Details["fromStateId"]);
                Assert.Equal("approved", applied.Details["toStateId"]);
            },
            contacted =>
            {
                Assert.Equal("applicant-contacted", contacted.Action);
                Assert.Equal("Applicant contacted", contacted.ActionDisplayName);
                Assert.Equal(DecisionMakerId, contacted.CreatedBy);
                Assert.Equal("Alice Example", contacted.CreatedByName);
                Assert.Equal(s_appliedAt.UtcDateTime, contacted.CreatedAt);
            },
            stopped =>
            {
                Assert.Equal("sla-clock-stopped", stopped.Action);
                Assert.Equal(s_appliedAt.ToString("O"), stopped.Details["stoppedAt"]);
            },
            issued =>
            {
                Assert.Equal("accreditation-issued", issued.Action);
                Assert.Equal(StepOneNumber, issued.Details["accreditationId"]);
                Assert.Equal("2025-03-04", issued.Details["startDate"]);
                Assert.Equal("2025", issued.Details["accreditationYear"]);
            }
        );
        // epr-rr9s: every entry snapshots the post-transition state.
        Assert.All(workItem.AuditLog, e => Assert.Equal("approved", e.StateId));

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
                "confirm-contact-approved",
                "approved-pending-contact",
                Arg.Any<ClaimsPrincipal>(),
                CancellationToken.None
            );
        await sut
            .NumberAdapter.DidNotReceiveWithAnyArgs()
            .GenerateOrUpdateAccreditationNumberAsync(default!, ct);
    }

    /// <summary>
    /// RA-133 / RA-527 task 007: the start date is 1 Jan of the configured
    /// year, or the day applicant contact is confirmed when that is later (an
    /// accreditation cannot start in the past). Stamped at step 2 from the
    /// caller's instant, with the configured year.
    /// </summary>
    [Theory]
    // configured year 2025, confirmed 2025-03-04 → the confirmation day.
    [InlineData(2025, "2025-03-04T09:15:00Z", "2025-03-04")]
    // configured year 2027 → Jan 1 of 2027 is later.
    [InlineData(2027, "2025-03-04T09:15:00Z", "2027-01-01")]
    // exactly on Jan 1.
    [InlineData(2027, "2027-01-01T00:00:00Z", "2027-01-01")]
    public async Task ApproveAsync_stamps_the_start_date_and_year_from_the_step_two_instant(
        int currentYear,
        string appliedAt,
        string expectedStartDate
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build(currentYear: currentYear);
        var workItem = BuildPendingContact();
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(
            workItem.Id,
            DecisionMaker(),
            DateTimeOffset.Parse(appliedAt, System.Globalization.CultureInfo.InvariantCulture),
            ct
        );

        Assert.True(result.IsSuccess, result.Message);
        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(workItem.Payload);
        Assert.Equal(
            DateOnly.Parse(expectedStartDate, System.Globalization.CultureInfo.InvariantCulture),
            payload.AccreditationStartDate
        );
        Assert.Equal(currentYear, payload.AccreditationYear);
        var issued = Assert.Single(workItem.AuditLog, a => a.Action == "accreditation-issued");
        Assert.Equal(expectedStartDate, issued.Details["startDate"]);
        Assert.Equal(
            currentYear.ToString(System.Globalization.CultureInfo.InvariantCulture),
            issued.Details["accreditationYear"]
        );
    }

    /// <summary>
    /// RA-527 task 007: an item that reached approved-pending-contact before
    /// step 1 stopped stamping the start date and year (dev/test data) has
    /// both overwritten at step 2. The number is kept.
    /// </summary>
    [Fact]
    public async Task ApproveAsync_overwrites_a_start_date_and_year_stamped_at_step_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build(currentYear: 2025);
        var workItem = BuildPendingContact(
            PendingPayloadWith(("accreditationStartDate", "2025-02-03"), ("accreditationYear", 2024))
        );
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), s_appliedAt, ct);

        Assert.True(result.IsSuccess, result.Message);
        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(workItem.Payload);
        Assert.Equal(new DateOnly(2025, 3, 4), payload.AccreditationStartDate);
        Assert.Equal(2025, payload.AccreditationYear);
        Assert.Equal(StepOneNumber, payload.AccreditationId);
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
        var workItem = BuildPendingContact();
        workItem.TemplateSnapshot = null;
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), s_appliedAt, ct);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("approved", workItem.StateId);
    }

    /// <summary>
    /// With neither a snapshot nor a registered type there are no rules to
    /// judge the step against. Refuse rather than approve blind.
    /// </summary>
    [Fact]
    public async Task ApproveAsync_refuses_when_no_template_can_be_resolved()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build(registerType: false);
        var workItem = BuildPendingContact();
        workItem.TemplateSnapshot = null;
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), s_appliedAt, ct);

        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    /// <summary>
    /// RA-527: the confirm-contact transition is checked against the item's
    /// own frozen snapshot, so an item still on rules without it is refused
    /// until the v17 snapshot migration patches it.
    /// </summary>
    [Fact]
    public async Task ApproveAsync_refuses_an_item_whose_snapshot_lacks_the_confirm_contact_transition()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildPendingContact();
        workItem.TemplateSnapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v16",
            States = workItem.TemplateSnapshot!.States,
            Transitions = workItem
                .TemplateSnapshot.Transitions.Where(t => t.ActionId != "confirm-contact-approved")
                .ToList(),
        };
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), s_appliedAt, ct);

        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        Assert.Equal("approved-pending-contact", workItem.StateId);
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    [Fact]
    public async Task ApproveAsync_preserves_unmodelled_payload_keys()
    {
        // RA-249: approval must MERGE the modelled updates over the existing
        // payload, not replace it. A full replace against the
        // [BsonIgnoreExtraElements] model dropped every unmodelled key
        // (applicationReference, source, siteAddress*), turning the
        // application ref into the work-item Guid downstream.
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildPendingContact(
            PendingPayloadWith(
                ("applicationReference", "RA-000000123"),
                ("source", "external-portal"),
                ("siteAddressLine1", "1 Recycling Way"),
                (
                    "siteAddress",
                    new BsonDocument { ["line1"] = "1 Recycling Way", ["postcode"] = "AB1 2CD" }
                )
            )
        );
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), s_appliedAt, ct);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("RA-000000123", workItem.Payload["applicationReference"].AsString);
        Assert.Equal("external-portal", workItem.Payload["source"].AsString);
        Assert.Equal("1 Recycling Way", workItem.Payload["siteAddressLine1"].AsString);
        var nested = workItem.Payload["siteAddress"].AsBsonDocument;
        Assert.Equal("1 Recycling Way", nested["line1"].AsString);
        Assert.Equal("AB1 2CD", nested["postcode"].AsString);
        Assert.Equal("Acme Ltd", workItem.Payload["organisationName"].AsString);
        Assert.Equal("EX-001", workItem.Payload["registrationNumber"].AsString);
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
        // Approval round-trips the payload through that
        // [BsonIgnoreExtraElements] model, so it is an operation that could
        // silently blank these fields. The merge is shallow: it survives today
        // precisely because `overseasSites` and `prns` are unmodelled. Adding
        // either to ReAccreditationPayload without deepening the merge would
        // drop every undeclared key nested inside it — this test is the guard
        // that turns that into a red build rather than a blank regulator page.
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildPendingContact(
            PendingPayloadWith(
                (
                    "overseasSites",
                    new BsonDocument
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
                    }
                ),
                (
                    "prns",
                    new BsonDocument
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
                    }
                )
            )
        );
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), s_appliedAt, ct);

        Assert.True(result.IsSuccess, result.Message);

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
    public async Task Queued_publishing_audit_runs_against_scoped_appender_with_accreditation_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildPendingContact();
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        Func<IServiceProvider, CancellationToken, Task>? captured = null;
        await sut.Queue.QueueAsync(
            Arg.Do<Func<IServiceProvider, CancellationToken, Task>>(j => captured = j),
            Arg.Any<CancellationToken>()
        );

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), s_appliedAt, ct);

        Assert.True(result.IsSuccess, result.Message);
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
                Arg.Is<Dictionary<string, string?>>(d => d["accreditationId"] == StepOneNumber),
                Arg.Any<ClaimsPrincipal>(),
                ct
            );
    }

    // ─────────────────────────── step 2 validation paths ──────────────────────

    [Fact]
    public async Task Returns_MissingActorIdentity_when_user_has_no_user_id_claim()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();

        var result = await sut.Service.ApproveAsync(Guid.NewGuid(), AnonymousUser(), s_appliedAt, ct);

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

        var result = await sut.Service.ApproveAsync(Guid.NewGuid(), DecisionMaker(), s_appliedAt, ct);

        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
    }

    [Fact]
    public async Task Succeeds_for_work_item_not_submitted_by_caller()
    {
        // RBAC (who may act on whose items) lives in the frontend now; the
        // service applies the action regardless of who submitted the item.
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem(
            stateId: "approved-pending-contact",
            submittedBy: OtherTenantClientId,
            payload: BuildPendingContact().Payload
        );
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), s_appliedAt, ct);

        Assert.True(result.IsSuccess, result.Message);
    }

    [Fact]
    public async Task Returns_UnknownAction_when_work_item_is_wrong_type()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem(stateId: "approved-pending-contact", typeId: "some-other-type");
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), s_appliedAt, ct);

        Assert.Equal(WorkItemActionFailureCode.UnknownAction, result.FailureCode);
    }

    [Theory]
    [InlineData("rejected", WorkItemActionFailureCode.TerminalState)]
    [InlineData("withdrawn", WorkItemActionFailureCode.TerminalState)]
    [InlineData("submitted", WorkItemActionFailureCode.InvalidTransition)]
    [InlineData("duly-made", WorkItemActionFailureCode.InvalidTransition)]
    [InlineData("assessment-in-progress", WorkItemActionFailureCode.InvalidTransition)]
    [InlineData("awaiting-decision", WorkItemActionFailureCode.InvalidTransition)]
    [InlineData("queried", WorkItemActionFailureCode.InvalidTransition)]
    public async Task ApproveAsync_refuses_outside_approved_pending_contact(
        string stateId,
        WorkItemActionFailureCode expected
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildPendingContact();
        workItem.StateId = stateId;
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), s_appliedAt, ct);

        Assert.Equal(expected, result.FailureCode);
        Assert.Empty(workItem.AuditLog);
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
        await sut.Queue.DidNotReceiveWithAnyArgs().QueueAsync(default!, ct);
    }

    /// <summary>
    /// An item already Granted (a retry, or a second click) is an idempotent
    /// replay: no re-stamping, no audit entries, no publishing, no email.
    /// </summary>
    [Fact]
    public async Task ApproveAsync_is_an_idempotent_replay_when_already_approved()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildPendingContact();
        workItem.StateId = "approved";
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), s_appliedAt, ct);

        Assert.True(result.IsSuccess);
        Assert.True(result.IsIdempotentReplay);
        Assert.Empty(workItem.AuditLog);
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
        await sut.Queue.DidNotReceiveWithAnyArgs().QueueAsync(default!, ct);
        await sut.Hooks[0]
            .DidNotReceiveWithAnyArgs()
            .OnActionAppliedAsync(default!, default!, default!, default!, ct);
    }

    /// <summary>
    /// Step 2 never mints: it applies the number step 1 stamped. An item
    /// pending contact without a real-format number (missing, or any other
    /// shape) is unexplained data needing a human, not a new number.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A25ER00000000000")]
    [InlineData("ACC-2025-A-DEADBEEF")]
    public async Task ApproveAsync_refuses_an_item_without_the_step_one_number(string? accreditationId)
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var payload = BuildPendingContact().Payload;
        if (accreditationId is null)
        {
            payload.Remove("accreditationId");
        }
        else
        {
            payload["accreditationId"] = accreditationId;
        }
        var workItem = BuildPendingContact(payload);
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), s_appliedAt, ct);

        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        Assert.Contains("Manual investigation required", result.Message);
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
        await sut
            .NumberAdapter.DidNotReceiveWithAnyArgs()
            .GenerateOrUpdateAccreditationNumberAsync(default!, ct);
    }

    [Fact]
    public async Task Returns_InvalidTransition_and_does_not_persist_when_existing_payload_is_corrupt()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        // Force the deserialiser to throw by stuffing a malformed value into
        // a typed field.
        var workItem = BuildPendingContact(
            PendingPayloadWith(("accreditationStartDate", "not-a-date"))
        );
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), s_appliedAt, ct);

        // A corrupt payload must NOT silently wipe existing data — the service
        // must abort and return a failure so the operator can investigate.
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
        Assert.Equal("not-a-date", workItem.Payload["accreditationStartDate"].AsString);
    }

    [Fact]
    public async Task Logs_and_continues_when_a_post_action_hook_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildPendingContact();
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

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), s_appliedAt, ct);

        Assert.True(result.IsSuccess, result.Message);
    }

    // ─────────────────────────── concurrency retry ─────────────────────

    [Fact]
    public async Task Retries_on_concurrency_conflict_and_succeeds_within_max_attempts()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        // Hand back a fresh work item per call so each retry sees a clean
        // pending-contact document (the production load would).
        sut.Persistence.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => BuildPendingContact());

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

        var result = await sut.Service.ApproveAsync(Guid.NewGuid(), DecisionMaker(), s_appliedAt, ct);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, calls);
        await sut.Queue.ReceivedWithAnyArgs(1).QueueAsync(default!, ct);
    }

    /// <summary>
    /// The guard runs again on every attempt: when a concurrent confirmation
    /// landed first, the retry sees Granted and replays — no second set of
    /// audit entries, no second publishing job or email.
    /// </summary>
    [Fact]
    public async Task A_concurrent_confirmation_that_landed_first_turns_the_retry_into_a_replay()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var granted = BuildPendingContact();
        granted.StateId = "approved";
        sut.Persistence.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => BuildPendingContact(), _ => granted);
        sut.Persistence.When(p => p.ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()))
            .Do(call => throw new WorkItemConcurrencyException(call.Arg<WorkItem>().Id, expectedVersion: 0));

        var result = await sut.Service.ApproveAsync(Guid.NewGuid(), DecisionMaker(), s_appliedAt, ct);

        Assert.True(result.IsSuccess);
        Assert.True(result.IsIdempotentReplay);
        await sut.Persistence.ReceivedWithAnyArgs(1).ReplaceAsync(default!, ct);
        await sut.Queue.DidNotReceiveWithAnyArgs().QueueAsync(default!, ct);
        await sut.Hooks[0]
            .DidNotReceiveWithAnyArgs()
            .OnActionAppliedAsync(default!, default!, default!, default!, ct);
    }

    [Fact]
    public async Task Returns_ConcurrencyConflict_after_three_failed_attempts()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        sut.Persistence.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => BuildPendingContact());
        sut.Persistence.When(p => p.ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                var item = call.Arg<WorkItem>();
                throw new WorkItemConcurrencyException(item.Id, expectedVersion: 0);
            });

        var result = await sut.Service.ApproveAsync(Guid.NewGuid(), DecisionMaker(), s_appliedAt, ct);

        Assert.Equal(WorkItemActionFailureCode.ConcurrencyConflict, result.FailureCode);
        await sut
            .Persistence.Received(3)
            .ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
        await sut.Queue.DidNotReceiveWithAnyArgs().QueueAsync(default!, ct);
    }

    [Fact]
    public async Task ApproveAsync_throws_when_user_is_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            sut.Service.ApproveAsync(Guid.NewGuid(), user: null!, s_appliedAt, ct)
        );
    }

    /// <summary>
    /// RA-551 regression: step 2 deserializes the payload into
    /// ReAccreditationPayload, stops the SLA clock, then merges it back via
    /// ToBsonDocument() - exactly the cycle that used to silently rewrite
    /// payload.nation from a string to its BSON ordinal int before Nation got
    /// [BsonRepresentation(BsonType.String)].
    /// </summary>
    [Theory]
    [InlineData(Nation.England)]
    [InlineData(Nation.Scotland)]
    [InlineData(Nation.Wales)]
    [InlineData(Nation.NorthernIreland)]
    public async Task ApproveAsync_does_not_corrupt_a_string_nation_to_an_int(Nation nation)
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildPendingContact(PendingPayloadWith(("nation", nation.ToString())));
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), s_appliedAt, ct);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(BsonType.String, workItem.Payload["nation"].BsonType);
        Assert.Equal(nation.ToString(), workItem.Payload["nation"].AsString);
    }

    // ──────────────── epr-r9oy / RA-448 phase 2: resolving the number ────────────────

    /// <summary>
    /// RA-690: the backend derives the reference from the application's own
    /// registration reference and year, so the request carries only the two
    /// ids (and the correlation id). The organisation id is passed through as
    /// held, whether a ReEx UUID or a numeric seeded id — it is never parsed.
    /// </summary>
    [Theory]
    [InlineData("500099")]
    [InlineData("c14854d9-e20a-41b3-87d5-a1fd4bbe2153")]
    public async Task ResolveAccreditationNumberAsync_requests_the_number_from_the_organisation_and_application_ids(
        string organisationId
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem(
            payload: new BsonDocument
            {
                ["organisationName"] = "Acme Ltd",
                ["operatorOrganisationId"] = organisationId,
                ["operatorApplicationId"] = "APP-500099",
                ["nation"] = "Scotland",
            }
        );
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);
        var correlationId = Guid.NewGuid();

        var result = await sut.Service.ResolveAccreditationNumberAsync(workItem.Id, correlationId, ct);

        Assert.True(result.IsSuccess);
        await sut
            .NumberAdapter.Received(1)
            .GenerateOrUpdateAccreditationNumberAsync(
                new AccreditationNumberRequest(organisationId, "APP-500099", correlationId),
                Arg.Any<CancellationToken>()
            );
    }

    /// <summary>
    /// RA-690: nation is no longer sent, so an item without one can still
    /// have its number resolved.
    /// </summary>
    [Fact]
    public async Task ResolveAccreditationNumberAsync_does_not_need_a_nation()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem(
            payload: new BsonDocument
            {
                ["operatorOrganisationId"] = "500027",
                ["operatorApplicationId"] = "APP-500027",
            }
        );
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ResolveAccreditationNumberAsync(workItem.Id, Guid.NewGuid(), ct);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        await sut
            .NumberAdapter.Received(1)
            .GenerateOrUpdateAccreditationNumberAsync(
                Arg.Any<AccreditationNumberRequest>(),
                Arg.Any<CancellationToken>()
            );
    }

    public static TheoryData<string, BsonDocument?> UnusablePayloads =>
        new()
        {
            {
                "organisation id blank",
                new BsonDocument
                {
                    ["operatorOrganisationId"] = "",
                    ["operatorApplicationId"] = "APP-500027",
                    ["nation"] = "England",
                }
            },
            {
                "application id blank",
                new BsonDocument
                {
                    ["operatorOrganisationId"] = "500027",
                    ["operatorApplicationId"] = "",
                    ["nation"] = "England",
                }
            },
            { "payload corrupt", new BsonDocument { ["accreditationStartDate"] = "not-a-date" } },
            { "payload null", null },
        };

    [Theory]
    [MemberData(nameof(UnusablePayloads))]
    public async Task ResolveAccreditationNumberAsync_fails_without_calling_the_adapter_for_an_unusable_payload(
        string because,
        BsonDocument? payload
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem();
        workItem.Payload = payload!;
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ResolveAccreditationNumberAsync(workItem.Id, Guid.NewGuid(), ct);

        Assert.False(result.IsSuccess, because);
        await sut
            .NumberAdapter.DidNotReceiveWithAnyArgs()
            .GenerateOrUpdateAccreditationNumberAsync(default!, ct);
    }

    [Fact]
    public async Task ResolveAccreditationNumberAsync_returns_the_adapters_number_without_mutating_the_work_item()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build(accreditationId: "A25ER5000270036WO");
        var workItem = BuildWorkItem();
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);
        var correlationId = Guid.NewGuid();

        var result = await sut.Service.ResolveAccreditationNumberAsync(
            workItem.Id,
            correlationId,
            ct
        );

        Assert.True(result.IsSuccess);
        Assert.Equal("A25ER5000270036WO", result.AccreditationNumber);
        Assert.Equal("awaiting-decision", workItem.StateId);
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
        await sut
            .NumberAdapter.Received(1)
            .GenerateOrUpdateAccreditationNumberAsync(
                Arg.Is<AccreditationNumberRequest>(r => r.CorrelationId == correlationId),
                ct
            );
    }

    [Fact]
    public async Task ResolveAccreditationNumberAsync_returns_failure_for_an_unknown_work_item()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var missingId = Guid.NewGuid();

        var result = await sut.Service.ResolveAccreditationNumberAsync(
            missingId,
            Guid.NewGuid(),
            ct
        );

        Assert.False(result.IsSuccess);
        await sut
            .NumberAdapter.DidNotReceiveWithAnyArgs()
            .GenerateOrUpdateAccreditationNumberAsync(default!, ct);
    }

    [Fact]
    public async Task ResolveAccreditationNumberAsync_returns_failure_when_the_adapter_call_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem();
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);
        sut.NumberAdapter.GenerateOrUpdateAccreditationNumberAsync(
                Arg.Any<AccreditationNumberRequest>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(AccreditationNumberResult.Failure("backend unreachable"));

        var result = await sut.Service.ResolveAccreditationNumberAsync(
            workItem.Id,
            Guid.NewGuid(),
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal("backend unreachable", result.ErrorMessage);
    }

    /// <summary>
    /// RA-690: with nation no longer needed, the two ids are the only
    /// required fields, and the failure names exactly those.
    /// </summary>
    [Theory]
    [InlineData("operatorOrganisationId")]
    [InlineData("operatorApplicationId")]
    public async Task ResolveAccreditationNumberAsync_returns_a_clear_failure_when_a_required_id_is_missing(
        string missingField
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var payload = new BsonDocument
        {
            ["organisationName"] = "Acme Ltd",
            ["operatorOrganisationId"] = "500027",
            ["operatorApplicationId"] = "APP-500027",
            ["nation"] = "England",
        };
        payload.Remove(missingField);
        var workItem = BuildWorkItem(payload: payload);
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ResolveAccreditationNumberAsync(
            workItem.Id,
            Guid.NewGuid(),
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "Work item is missing data required to request an accreditation number "
                + "(operatorOrganisationId or operatorApplicationId).",
            result.ErrorMessage
        );
        await sut
            .NumberAdapter.DidNotReceiveWithAnyArgs()
            .GenerateOrUpdateAccreditationNumberAsync(default!, ct);
    }

    // ──────────────── RA-527: step 1 — record an approved determination ────────────────

    private static readonly AccreditationNumberResult s_mintedNumber =
        AccreditationNumberResult.Success("A25ER5000270036WO");

    /// <summary>
    /// RA-527 task 007: step 1 stamps only the accreditation id. The start
    /// date and year are stamped at step 2 (confirm applicant contact), as
    /// the old single-step approve did at its one write. Unmodelled keys
    /// survive the payload round-trip (RA-249).
    /// </summary>
    [Fact]
    public async Task RecordApprovedDeterminationAsync_stamps_only_the_accreditation_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build(currentYear: 2025);
        var workItem = BuildWorkItem(
            payload: new BsonDocument
            {
                ["organisationName"] = "Acme Ltd",
                ["operatorOrganisationId"] = "500027",
                ["operatorApplicationId"] = "APP-500027",
                ["nation"] = "England",
                ["applicationReference"] = "RA-000000999",
            }
        );
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.RecordApprovedDeterminationAsync(
            workItem.Id, DecisionMaker(), s_mintedNumber, ct);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("RA-000000999", workItem.Payload["applicationReference"].AsString);
        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(workItem.Payload);
        Assert.Equal("A25ER5000270036WO", payload.AccreditationId);
        Assert.Null(payload.AccreditationStartDate);
        Assert.Null(payload.AccreditationYear);
    }

    /// <summary>
    /// RA-527 task 007 (restored from main's single-step approve): an item
    /// that already carries an accreditation id but is not approved is
    /// unexplained data needing a human, not a second number. Nothing is
    /// written. Only the retired local generator's known legacy shape (16
    /// characters, not the real format) is treated as unset.
    /// </summary>
    [Theory]
    [InlineData("A25ER5000270036WO")]
    [InlineData("ACC-2025-A-DEADBEEF")]
    public async Task RecordApprovedDeterminationAsync_refuses_an_item_that_already_carries_an_accreditation_id(
        string existingId
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem();
        workItem.Payload["accreditationId"] = existingId;
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.RecordApprovedDeterminationAsync(
            workItem.Id, DecisionMaker(), AccreditationNumberResult.Success("A25ER5000270099WO"), ct);

        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        Assert.Contains("Manual investigation required", result.Message);
        Assert.Equal("awaiting-decision", workItem.StateId);
        Assert.Equal(existingId, workItem.Payload["accreditationId"].AsString);
        Assert.Empty(workItem.AuditLog);
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    [Fact]
    public async Task RecordApprovedDeterminationAsync_treats_a_legacy_format_id_as_unset()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem();
        workItem.Payload["accreditationId"] = "A25ER00000000000";
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.RecordApprovedDeterminationAsync(
            workItem.Id, DecisionMaker(), s_mintedNumber, ct);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("approved-pending-contact", workItem.StateId);
        Assert.Equal("A25ER5000270036WO", workItem.Payload["accreditationId"].AsString);
    }

    /// <summary>RA-551 regression, for the step 1 payload round-trip.</summary>
    [Theory]
    [InlineData(Nation.England)]
    [InlineData(Nation.NorthernIreland)]
    public async Task RecordApprovedDeterminationAsync_does_not_corrupt_a_string_nation_to_an_int(Nation nation)
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem();
        workItem.Payload["nation"] = nation.ToString();
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.RecordApprovedDeterminationAsync(
            workItem.Id, DecisionMaker(), s_mintedNumber, ct);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(BsonType.String, workItem.Payload["nation"].BsonType);
        Assert.Equal(nation.ToString(), workItem.Payload["nation"].AsString);
    }

    /// <summary>
    /// RA-527 step 1: the number minted by the caller is stamped (alone: the
    /// start date and year wait for step 2), the item moves to the non-terminal
    /// approved-pending-contact state and the determination is audited. The
    /// outcome is NOT applied yet: no SLA clock stop, no accreditation-issued
    /// entry, no publishing job.
    /// </summary>
    [Fact]
    public async Task RecordApprovedDeterminationAsync_stamps_the_number_and_moves_to_approved_pending_contact()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem();
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.RecordApprovedDeterminationAsync(
            workItem.Id, DecisionMaker(), s_mintedNumber, ct);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("approved-pending-contact", workItem.StateId);
        Assert.Equal(s_fixedNow.UtcDateTime, workItem.LastModifiedAt);

        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(workItem.Payload);
        Assert.Equal("A25ER5000270036WO", payload.AccreditationId);
        // The start date and year wait for step 2 (RA-527 task 007).
        Assert.Null(payload.AccreditationStartDate);
        Assert.Null(payload.AccreditationYear);
        // The SLA clock keeps running until the outcome is applied (step 2).
        Assert.Null(payload.SlaClock?.StoppedAt);
        Assert.Equal("Acme Ltd", payload.OrganisationName);

        Assert.Collection(
            workItem.AuditLog,
            applied =>
            {
                Assert.Equal("action-applied", applied.Action);
                Assert.Equal("make-determination-approved", applied.Details["actionId"]);
                Assert.Equal("awaiting-decision", applied.Details["fromStateId"]);
                Assert.Equal("approved-pending-contact", applied.Details["toStateId"]);
            },
            determination =>
            {
                Assert.Equal("determination-made", determination.Action);
                Assert.Equal("Determination made: Approved", determination.ActionDisplayName);
                Assert.Equal("approved", determination.Details["outcome"]);
                Assert.Equal("A25ER5000270036WO", determination.Details["accreditationId"]);
                Assert.Equal(DecisionMakerId, determination.CreatedBy);
                Assert.Equal("Alice Example", determination.CreatedByName);
            }
        );
        Assert.All(workItem.AuditLog, e => Assert.Equal("approved-pending-contact", e.StateId));

        await sut.Persistence.Received(1).ReplaceAsync(workItem, Arg.Any<CancellationToken>());
        await sut.Queue.DidNotReceiveWithAnyArgs().QueueAsync(default!, ct);
        await sut.NumberAdapter.DidNotReceiveWithAnyArgs().GenerateOrUpdateAccreditationNumberAsync(default!, ct);
        await sut.Hooks[0]
            .Received(1)
            .OnActionAppliedAsync(
                workItem,
                "make-determination-approved",
                "awaiting-decision",
                Arg.Any<ClaimsPrincipal>(),
                CancellationToken.None
            );
    }

    /// <summary>
    /// A replay of the same outcome: on the pending state, or (RA-527 task
    /// 007, as on main) on Granted, which stays Granted.
    /// </summary>
    [Theory]
    [InlineData("approved-pending-contact")]
    [InlineData("approved")]
    public async Task RecordApprovedDeterminationAsync_is_an_idempotent_replay_when_already_approved_at_either_step(
        string stateId
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildPendingContact();
        workItem.StateId = stateId;
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.RecordApprovedDeterminationAsync(
            workItem.Id, DecisionMaker(), AccreditationNumberResult.Success("A25ER5000270099WO"), ct);

        Assert.True(result.IsSuccess);
        Assert.True(result.IsIdempotentReplay);
        Assert.Empty(workItem.AuditLog);
        // The step 1 number is never overwritten (RA-527 task 007).
        Assert.Equal(StepOneNumber, workItem.Payload["accreditationId"].AsString);
        Assert.Equal(stateId, workItem.StateId);
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    [Theory]
    [InlineData("rejected", WorkItemActionFailureCode.TerminalState)]
    [InlineData("withdrawn", WorkItemActionFailureCode.TerminalState)]
    [InlineData("assessment-in-progress", WorkItemActionFailureCode.InvalidTransition)]
    [InlineData("queried", WorkItemActionFailureCode.InvalidTransition)]
    public async Task RecordApprovedDeterminationAsync_refuses_outside_awaiting_decision(
        string stateId,
        WorkItemActionFailureCode expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem(stateId: stateId);
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.RecordApprovedDeterminationAsync(
            workItem.Id, DecisionMaker(), s_mintedNumber, ct);

        Assert.Equal(expected, result.FailureCode);
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    [Fact]
    public async Task RecordApprovedDeterminationAsync_retries_on_concurrency_conflict()
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
                    throw new WorkItemConcurrencyException(call.Arg<WorkItem>().Id, expectedVersion: 0);
                }
            });

        var result = await sut.Service.RecordApprovedDeterminationAsync(
            Guid.NewGuid(), DecisionMaker(), s_mintedNumber, ct);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RecordApprovedDeterminationAsync_returns_MissingActorIdentity_without_a_user_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();

        var result = await sut.Service.RecordApprovedDeterminationAsync(
            Guid.NewGuid(), AnonymousUser(), s_mintedNumber, ct);

        Assert.Equal(WorkItemActionFailureCode.MissingActorIdentity, result.FailureCode);
        await sut.Persistence.DidNotReceiveWithAnyArgs().GetByIdAsync(default, ct);
    }
}
