using System.Security.Claims;
using System.Text.Json;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;

public sealed record AddOverseasSiteWithdrawal(string RequestId, string ProposedSiteId, JsonElement Target);

public sealed class AddOverseasSiteWithdrawalService(IWorkItemPersistence persistence, IWorkItemService engine)
{
    public async Task<WorkItemActionResult> Withdraw(Guid id, AddOverseasSiteWithdrawal withdrawal,
        ClaimsPrincipal user, CancellationToken ct)
    {
        var item = await persistence.GetByIdAsync(id, ct);
        if (item is null || item.TypeId != "add-overseas-site")
            return WorkItemActionResult.Failure(WorkItemActionFailureCode.WorkItemNotFound, "Add ORS case not found.");

        var target = item.Payload["target"].AsBsonDocument;
        if (user.FindFirstValue("client_id") != "epr-register-enrol-backend"
            || user.FindFirstValue("user:id") != target["organisationId"].AsString
            || item.Payload["operatorRequestId"].AsString != withdrawal.RequestId
            || item.Payload["proposedSiteId"].AsString != withdrawal.ProposedSiteId
            || withdrawal.Target.ValueKind != JsonValueKind.Object
            || !MongoDB.Bson.BsonDocument.Parse(withdrawal.Target.GetRawText()).Equals(target))
            return WorkItemActionResult.Failure(WorkItemActionFailureCode.InvalidSubmission,
                "Withdrawal must match the operator request, site and accreditation target.");

        // Reconfirm the same closed case after a lost response without another transition or audit.
        if (item.StateId == "withdrawn") return WorkItemActionResult.IdempotentReplay(item);
        return await engine.ApplyActionAsync(id, "withdraw", user, ct);
    }
}
