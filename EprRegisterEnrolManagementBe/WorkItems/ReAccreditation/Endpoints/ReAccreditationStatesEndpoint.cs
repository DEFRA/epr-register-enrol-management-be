using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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
/// decides, per <see cref="ReAccreditationOperatorJourneyPush"/>.
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

    internal static string OperatorJourneyPushFor(IWorkItemType type, string stateId)
    {
        var transitionsIn = type
            .Transitions.Where(t => string.Equals(t.ToStateId, stateId, StringComparison.Ordinal))
            .ToList();

        // The query push (ReAccreditationQueryPushHook) owns the moves its
        // query actions make; StandardStatusPushHook excludes their target.
        if (transitionsIn.Any(t => ReAccreditationQueryPushHook.IsQueryPushAction(t.ActionId)))
        {
            return ReAccreditationOperatorJourneyPush.Query;
        }

        // A state no transition moves onto is never the target of a push.
        // ('submitted' is not one: continue-review-during-duly-making moves an
        // item back onto it, and that move is pushed.)
        return transitionsIn.Count > 0 && StandardStatusPushHook.IsStatusPushedTargetState(stateId)
            ? ReAccreditationOperatorJourneyPush.Status
            : ReAccreditationOperatorJourneyPush.None;
    }
}

/// <summary>
/// RA-751: values of <see cref="ReAccreditationStateResponse.OperatorJourneyPush"/>.
/// </summary>
internal static class ReAccreditationOperatorJourneyPush
{
    /// <summary>
    /// A move onto this state is pushed to the operator backend's
    /// <c>/case-management/{workItemId}/status</c> endpoint with
    /// <c>toStateId</c> set to the state id.
    /// </summary>
    public const string Status = "status";

    /// <summary>A move onto this state is sent by the separate query push.</summary>
    public const string Query = "query";

    /// <summary>
    /// Never pushed: the operator journey keeps the status it already had.
    /// </summary>
    public const string None = "none";
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
    string OperatorJourneyPush
);
