using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EprRegisterEnrolManagementBe.Auth;
using EprRegisterEnrolManagementBe.Test.Auth;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using NSubstitute;

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
        // submit-for-decision (the normal route in) is not pushed;
        // continue-review-during-decision is.
        ("awaiting-decision", "Awaiting decision", false, "partial"),
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
        // Read the wire JSON, so the push kind is asserted as the string sent.
        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken),
            cancellationToken: TestContext.Current.CancellationToken
        );
        var root = json.RootElement;
        Assert.Equal(ReAccreditationType.Id, root.GetProperty("typeId").GetString());
        Assert.Equal(new ReAccreditationType().TemplateVersion, root.GetProperty("templateVersion").GetString());
        Assert.Equal(
            s_expected,
            root.GetProperty("states")
                .EnumerateArray()
                .Select(s =>
                    (
                        s.GetProperty("id").GetString()!,
                        s.GetProperty("displayName").GetString()!,
                        s.GetProperty("isTerminal").GetBoolean(),
                        s.GetProperty("operatorJourneyPush").GetString()!
                    )
                )
                .ToArray()
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

    [Fact]
    public void An_unregistered_type_is_a_500_with_an_error_log()
    {
        var registry = Substitute.For<IWorkItemRegistry>();
        registry.Find(ReAccreditationType.Id).Returns((IWorkItemType?)null);
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logs));

        var result = ReAccreditationStatesEndpoint.GetStates(registry, loggerFactory);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, problem.StatusCode);
        Assert.Equal("Work item type not registered", problem.ProblemDetails.Title);
        var log = Assert.Single(logs.Records);
        Assert.Equal(LogLevel.Error, log.Level);
        Assert.Equal(ReAccreditationStatesEndpoint.LoggerCategory, log.Category);
        Assert.Equal(ReAccreditationType.Id, log.Properties["TypeId"]);
    }

    [Fact]
    public void A_state_is_query_pushed_when_a_query_push_action_moves_onto_it()
    {
        // Derived from the transitions and ReAccreditationQueryPushHook's
        // action set, not from a copy of the state id: rename the state and
        // the answer follows it.
        var type = Substitute.For<IWorkItemType>();
        type.Transitions.Returns(
            [new WorkItemTransition("query-during-duly-made", "Query", "duly-made", "on-hold")]
        );

        Assert.Equal(
            OperatorJourneyPushKind.Query,
            ReAccreditationStatesEndpoint.OperatorJourneyPushFor(type, "on-hold")
        );
        Assert.Equal(
            OperatorJourneyPushKind.None,
            ReAccreditationStatesEndpoint.OperatorJourneyPushFor(type, "queried")
        );
    }

    [Fact]
    public void A_state_reached_by_pushed_and_unpushed_routes_is_partial()
    {
        var type = Substitute.For<IWorkItemType>();
        type.Transitions.Returns(
            [
                new WorkItemTransition("submit-for-decision", "Submit", "updated", "awaiting-decision"),
                new WorkItemTransition("continue-review-during-decision", "Continue", "updated", "awaiting-decision"),
                // A self-loop is an in-place action, not a move onto the state.
                new WorkItemTransition("change-deadline", "Change", "awaiting-decision", "awaiting-decision"),
            ]
        );

        Assert.Equal(
            OperatorJourneyPushKind.Partial,
            ReAccreditationStatesEndpoint.OperatorJourneyPushFor(type, "awaiting-decision")
        );
    }
}
