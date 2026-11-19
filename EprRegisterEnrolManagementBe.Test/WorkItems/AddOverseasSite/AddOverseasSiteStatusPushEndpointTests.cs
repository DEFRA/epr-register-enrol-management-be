using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using static EprRegisterEnrolManagementBe.Test.TestSupport.AddOverseasSiteAppFactory;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.AddOverseasSite;

/// <summary>
/// RA-495: the operator journey is told about an add-ORS work item's progress
/// on its change-request status route, never the accreditation-application
/// route re-accreditation uses. Transitions are pushed after they commit, in
/// the background, with re-accreditation's skips (starting the assessment and
/// step 1 of a determination are not pushed). Confirm contact pushes the final
/// decision before anything is written, and commits only once the operator
/// journey has applied it. Driven through the real ASP.NET pipeline with the
/// production modules registered and a fake push adapter, against ephemeral
/// MongoDB.
/// </summary>
public sealed class AddOverseasSiteStatusPushEndpointTests(MongoIntegrationFixture fixture)
{
    private const OperatorBackendStatusRoute ChangeRequest = OperatorBackendStatusRoute.ChangeRequest;
    private const OperatorBackendPushPipeline BestEffort = OperatorBackendPushPipeline.BestEffort;
    private const OperatorBackendPushPipeline Decision = OperatorBackendPushPipeline.Decision;

    [Fact]
    public async Task Duly_making_is_pushed_to_the_change_request_status_route_after_it_commits()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = (await factory.SubmitAsync(client, "push-duly-make", siteCount: 1, ct))[0];

        Assert.Equal(HttpStatusCode.OK, (await PostDulyMakeAsync(client, id, ct)).StatusCode);

        var pushed = await factory.WaitForAuditAsync(id, "status-push-sent", ct);
        Assert.Equal([new Push(ChangeRequest, BestEffort, id, "submitted", "duly-made", "duly-make")], factory.Pushes);
        var sent = Assert.Single(pushed.AuditLog, a => a.Action == "status-push-sent");
        Assert.Equal("duly-made", sent.Details["toStateId"]);
        Assert.Equal("duly-make", sent.Details["actionId"]);
    }

    [Theory]
    [InlineData("approved", "confirm-contact-approved")]
    [InlineData("rejected", "confirm-contact-rejected")]
    public async Task Only_duly_making_and_the_final_decision_reach_the_operator_journey(
        string outcome,
        string confirmActionId
    )
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await factory.DulyMadeAndStartedAsync(client, $"push-{outcome}", ct);

        Assert.Equal(HttpStatusCode.OK, (await PostDecisionAsync(client, id, outcome, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostConfirmContactAsync(client, id, ct)).StatusCode);

        var closed = await factory.WaitForAuditAsync(id, "status-push-sent", ct, count: 2);
        Assert.Equal(outcome, closed.StateId);
        // AC6: the decision carries the effective date, the one entered on approval or the
        // submission date on refusal.
        var effectiveDate = outcome == "approved" ? EffectiveDate : UkCalendar.DateOf(closed.SubmittedAt);
        // Starting the assessment and the pending-contact step are never queued,
        // so nothing more can arrive later.
        Assert.Equal(
            [
                new Push(ChangeRequest, BestEffort, id, "submitted", "duly-made", "duly-make"),
                new Push(
                    ChangeRequest,
                    Decision,
                    id,
                    $"{outcome}-pending-contact",
                    outcome,
                    confirmActionId,
                    effectiveDate
                ),
            ],
            factory.Pushes
        );
    }

    [Fact]
    public async Task Confirm_contact_commits_nothing_when_the_push_fails_and_a_retry_completes_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(
            fixture,
            OperatorBackendPushResult.Failure("Operator backend returned 500."),
            OperatorBackendPushResult.Success()
        );
        using var client = factory.CreateClient();
        var id = await factory.DulyMadeAndStartedAsync(client, "push-confirm-retry", ct);
        Assert.Equal(HttpStatusCode.OK, (await PostDecisionAsync(client, id, "approved", ct)).StatusCode);

        var failed = await PostConfirmContactAsync(client, id, ct);

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        var untouched = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal("approved-pending-contact", untouched!.StateId);
        Assert.DoesNotContain(untouched.AuditLog, a => a.Action == "applicant-contacted");
        var pushFailed = Assert.Single(untouched.AuditLog, a => a.Action == "status-push-failed");
        Assert.Equal("approved", pushFailed.Details["toStateId"]);
        Assert.Equal("Operator backend returned 500.", pushFailed.Details["errorMessage"]);

        var retried = await PostConfirmContactAsync(client, id, ct);

        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        var granted = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal("approved", granted!.StateId);
        Assert.Contains(granted.AuditLog, a => a.Action == "applicant-contacted");
        Assert.Contains(
            granted.AuditLog,
            a => a.Action == "status-push-sent" && a.Details["actionId"] == "confirm-contact-approved"
        );
        Assert.Equal(2, factory.Pushes.Count(p => p is { Route: ChangeRequest, Pipeline: Decision }));
    }
}
