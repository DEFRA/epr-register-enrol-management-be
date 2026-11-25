using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Endpoints;

/// <summary>
/// RA-751: <c>GET /work-items/re-accreditation/states</c>. Lists the
/// re-accreditation states of the current template (id, display name,
/// terminal flag) in workflow order, with how a move onto each state reaches
/// the operator journey. The Admin UI reads this instead of keeping its own
/// copy (RA-751 ACs 2, 3 and 6).
///
/// Management-be does not push a status name: it pushes the state id as
/// <c>toStateId</c>, and the operator backend maps that id onto its own
/// <c>ApplicationStatus</c>. So this endpoint says only what management-be
/// decides, per <see cref="OperatorJourneyPushKind"/>.
/// </summary>
internal static class ReAccreditationStatesEndpoint
{
    // A static class cannot be a logger type argument, so name the category.
    internal const string LoggerCategory =
        "EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Endpoints.ReAccreditationStatesEndpoint";

    internal static Results<Ok<ReAccreditationStatesResponse>, ProblemHttpResult> GetStates(
        [FromServices] IWorkItemRegistry registry,
        [FromServices] ILoggerFactory loggerFactory
    )
    {
        // ReAccreditationModule registers the type, so this lookup failing is
        // a wiring defect: log it and report a 500 rather than an empty list.
        if (registry.Find(ReAccreditationType.Id) is not { } type)
        {
            loggerFactory.CreateLogger(LoggerCategory).LogError(
                "GET /work-items/re-accreditation/states: work item type {TypeId} is not registered.",
                ReAccreditationType.Id
            );
            return TypedResults.Problem(
                title: "Work item type not registered",
                detail: $"Work item type '{ReAccreditationType.Id}' is not registered.",
                statusCode: StatusCodes.Status500InternalServerError
            );
        }

        var states = type
            .States.Select(state => new ReAccreditationStateResponse(
                state.Id,
                state.DisplayName,
                state.IsTerminal,
                OperatorJourneyPushFor(type, state.Id)
            ))
            .ToList();

        return TypedResults.Ok(
            new ReAccreditationStatesResponse(type.TypeId, type.TemplateVersion, states)
        );
    }

    /// <summary>
    /// How a move onto <paramref name="stateId"/> reaches the operator journey,
    /// derived per action from the transitions into it (self-loops are
    /// in-place actions, not moves onto the state): an action
    /// <see cref="ReAccreditationQueryPushHook"/> pushes on is
    /// <see cref="OperatorJourneyPushKind.Query"/>, an action
    /// <see cref="StandardStatusPushHook.IsActionStatusPushed"/> reports is
    /// <see cref="OperatorJourneyPushKind.Status"/>, anything else is
    /// <see cref="OperatorJourneyPushKind.None"/>. When the routes in disagree
    /// the state is <see cref="OperatorJourneyPushKind.Partial"/>: whether the
    /// operator was told depends on which route the item took.
    /// </summary>
    internal static OperatorJourneyPushKind OperatorJourneyPushFor(IWorkItemType type, string stateId)
    {
        var kinds = type
            .Transitions.Where(t =>
                string.Equals(t.ToStateId, stateId, StringComparison.Ordinal)
                && !string.Equals(t.FromStateId, t.ToStateId, StringComparison.Ordinal)
            )
            .Select(t => PushKindOf(type, t.ActionId))
            .Distinct()
            .ToList();

        return kinds.Count switch
        {
            0 => OperatorJourneyPushKind.None,
            1 => kinds[0],
            _ => OperatorJourneyPushKind.Partial,
        };
    }

    private static OperatorJourneyPushKind PushKindOf(IWorkItemType type, string actionId)
    {
        if (ReAccreditationQueryPushHook.IsQueryPushAction(actionId))
        {
            return OperatorJourneyPushKind.Query;
        }
        return StandardStatusPushHook.IsActionStatusPushed(type, actionId)
            ? OperatorJourneyPushKind.Status
            : OperatorJourneyPushKind.None;
    }
}

/// <summary>
/// RA-751: values of <see cref="ReAccreditationStateResponse.OperatorJourneyPush"/>,
/// serialised as lower-case strings.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<OperatorJourneyPushKind>))]
public enum OperatorJourneyPushKind
{
    /// <summary>Never pushed: the operator journey keeps the status it had.</summary>
    [JsonStringEnumMemberName("none")]
    None,

    /// <summary>
    /// Pushed to the operator backend's <c>/case-management/{workItemId}/status</c>
    /// endpoint with <c>toStateId</c> set to the state id.
    /// </summary>
    [JsonStringEnumMemberName("status")]
    Status,

    /// <summary>Sent by the separate query push.</summary>
    [JsonStringEnumMemberName("query")]
    Query,

    /// <summary>
    /// Some routes into the state push and some do not (awaiting-decision:
    /// submit-for-decision is not pushed, continue-review-during-decision is).
    /// </summary>
    [JsonStringEnumMemberName("partial")]
    Partial,
}

public sealed record ReAccreditationStatesResponse(
    string TypeId,
    string TemplateVersion,
    IReadOnlyList<ReAccreditationStateResponse> States
);

public sealed record ReAccreditationStateResponse(
    string Id,
    string DisplayName,
    bool IsTerminal,
    OperatorJourneyPushKind OperatorJourneyPush
);
