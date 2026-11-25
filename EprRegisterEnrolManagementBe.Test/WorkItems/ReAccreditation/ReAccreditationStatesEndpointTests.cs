using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolManagementBe.Auth;
using EprRegisterEnrolManagementBe.Test.Auth;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Endpoints;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-751 ACs 2, 3 and 6: <c>GET /work-items/re-accreditation/states</c> lists
/// the 12 re-accreditation states with their ids beside their display names
/// (some display names are shared), and how a move onto each state reaches
/// the operator journey.
/// </summary>
public class ReAccreditationStatesEndpointTests
{
    // Expected per state, from StandardStatusPushHook's exclusions and the
    // separate query push: "e.g. assessment-in-progress and updated both push".
    private static readonly (string Id, string DisplayName, bool IsTerminal, string Push)[] s_expected =
    [
        // continue-review-during-duly-making (updated → submitted) is pushed.
        ("submitted", "Not started", false, "status"),
        ("duly-made", "Duly made", false, "status"),
        ("assessment-started", "Duly made", false, "none"),
        ("assessment-in-progress", "Updated", false, "status"),
        ("awaiting-decision", "Awaiting decision", false, "status"),
        ("queried", "Queried", false, "query"),
        ("updated", "Updated", false, "status"),
        ("approved-pending-contact", "Approved", false, "none"),
        ("approved", "Granted", true, "status"),
        ("rejected-pending-contact", "Refused", false, "none"),
        ("rejected", "Refused", true, "status"),
        ("withdrawn", "Withdrawn", true, "none"),
    ];

    [Fact]
    public async Task Lists_every_re_accreditation_state_in_workflow_order_with_its_operator_journey_push()
    {
        await using var factory = new BareFactory(environment: "Development");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, "frontend");

        var response = await client.GetAsync(
            "/work-items/re-accreditation/states",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ReAccreditationStatesResponse>(
            TestContext.Current.CancellationToken
        );
        Assert.Equal(ReAccreditationType.Id, body!.TypeId);
        Assert.Equal(new ReAccreditationType().TemplateVersion, body.TemplateVersion);
        Assert.Equal(
            s_expected,
            body.States.Select(s => (s.Id, s.DisplayName, s.IsTerminal, s.OperatorJourneyPush)).ToArray()
        );
    }

    [Fact]
    public async Task Requires_an_authenticated_caller()
    {
        await using var factory = new BareFactory(environment: "Development");
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            "/work-items/re-accreditation/states",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
