using System.Security.Claims;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// One <c>POST /work-items</c> submission, as
/// <see cref="IWorkItemService.SubmitAsync"/> and
/// <see cref="IWorkItemService.SubmitPerSiteAsync"/> take it.
/// </summary>
/// <param name="Type">The registered type being submitted.</param>
/// <param name="Payload">The submitted payload. The engine stamps server-generated fields onto it.</param>
/// <param name="SubmittedBy">The calling client's id, or <c>null</c> when unknown.</param>
/// <param name="User">The caller. Must carry a <c>user:id</c> claim.</param>
/// <param name="Metadata">
/// Optional caller-supplied audit context (RA-126), currently only <c>source</c>.
/// </param>
public sealed record WorkItemSubmissionRequest(
    IWorkItemType Type,
    BsonDocument Payload,
    string? SubmittedBy,
    ClaimsPrincipal User,
    IReadOnlyDictionary<string, string?>? Metadata = null
);
