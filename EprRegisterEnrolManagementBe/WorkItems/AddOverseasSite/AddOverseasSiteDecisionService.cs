using System.Net.Http.Json;
using System.Security.Claims;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Options;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;

public sealed class AddOverseasSiteDecisionService(IWorkItemPersistence persistence,
    IWorkItemService engine, IHttpClientFactory clients, IOptions<OperatorBackendApiConfig> options,
    ILogger<AddOverseasSiteDecisionService> logger)
{
    public async Task<WorkItemActionResult> StartAssessment(Guid id, ClaimsPrincipal user, CancellationToken ct)
    {
        var item = await persistence.GetByIdAsync(id, ct);
        if (item is null || item.TypeId != "add-overseas-site") return NotFound();
        return await engine.ApplyActionAsync(id, "start-assessment", user, ct);
    }

    public Task<WorkItemActionResult> Refuse(Guid id, ClaimsPrincipal user, CancellationToken ct) =>
        Decide(id, "reject", "rejected", user, ct);

    public Task<WorkItemActionResult> Approve(Guid id, ClaimsPrincipal user, CancellationToken ct) =>
        Decide(id, "approve", "approved", user, ct);

    private async Task<WorkItemActionResult> Decide(Guid id, string actionId, string outcome,
        ClaimsPrincipal user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(user.FindFirstValue("user:id")))
            return WorkItemActionResult.Failure(WorkItemActionFailureCode.MissingActorIdentity, "An acting caseworker is required.");
        var item = await persistence.GetByIdAsync(id, ct);
        if (item is null || item.TypeId != "add-overseas-site") return NotFound();
        if (item.StateId != outcome)
        {
            var decision = await engine.ApplyActionAsync(id, actionId, user, ct);
            if (!decision.IsSuccess) return decision;
            item = decision.WorkItem!;
        }
        if (item.Payload.GetValue("outcomeDeliveryConfirmed", false) == true)
            return WorkItemActionResult.IdempotentReplay(item);

        // Commit the engine decision first; an unacknowledged callback can retry only this recorded outcome.
        var config = options.Value;
        try
        {
            if (!config.Enabled || string.IsNullOrWhiteSpace(config.Url) || string.IsNullOrWhiteSpace(config.SharedSecret))
                throw new HttpRequestException("Signed operator outcome delivery is not configured.");
            using var message = new HttpRequestMessage(HttpMethod.Post,
                config.Url.TrimEnd('/') + "/api/v1/accreditation-changes/outcome");
            OperatorBackendSigning.AddHeaders(message, config);
            message.Content = JsonContent.Create(new
            {
                requestId = item.Payload["operatorRequestId"].AsString,
                caseId = item.Id.ToString(),
                proposedSiteId = item.Payload["proposedSiteId"].AsString,
                target = WorkItemPayloadConverter.ToJson(item.Payload["target"].AsBsonDocument),
                outcome = item.StateId,
            }, options: OperatorBackendJson.Options);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(config.RequestTimeoutSeconds));
            using var response = await clients.CreateClient("DefaultClient").SendAsync(message, timeout.Token);
            response.EnsureSuccessStatusCode();
            await persistence.SetPayloadFieldAsync(id, "outcomeDeliveryConfirmed", BsonBoolean.True, ct);
            logger.LogInformation("Add ORS outcome acknowledged for case {CaseId}", id);
            return WorkItemActionResult.Success((await persistence.GetByIdAsync(id, ct))!);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            logger.LogWarning(ex, "Add ORS outcome delivery unconfirmed for case {CaseId}; retry the recorded {Outcome}", id, outcome);
            return WorkItemActionResult.Failure(WorkItemActionFailureCode.UpstreamNotificationFailed,
                "Decision recorded. Operator delivery is unconfirmed; retry this decision.");
        }
    }

    private static WorkItemActionResult NotFound() =>
        WorkItemActionResult.Failure(WorkItemActionFailureCode.WorkItemNotFound, "Add ORS case not found.");
}
