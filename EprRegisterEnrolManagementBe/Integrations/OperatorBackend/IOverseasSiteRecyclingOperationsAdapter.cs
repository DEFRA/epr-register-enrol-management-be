namespace EprRegisterEnrolManagementBe.Integrations.OperatorBackend;

/// <summary>
/// RA-469 AC16: updates the recycling operation codes for one overseas
/// reprocessing site (ORS) on an accreditation application, on behalf of a
/// regulator correcting them during case review.
///
/// Unlike <see cref="IAccreditationNumberAdapter"/> (which intentionally
/// signs with a null identity — see its call site), this adapter signs
/// with the REAL caller's user id/name via <c>OperatorBackendSigning.AddHeaders</c>'s
/// optional userId/userName parameters, so the backend's audit record
/// (AC15/AC19) attributes the edit to the actual regulator rather than a
/// null/service identity.
///
/// Never throws its way out — mirrors <see cref="IAccreditationNumberAdapter"/>'s
/// "hard pre-commit gate reported as a typed result" convention: a failed
/// call here means the caller (the recycling-operations endpoint) must
/// abandon the operation and report a clean error, not proceed with
/// nothing.
/// </summary>
public interface IOverseasSiteRecyclingOperationsAdapter
{
    /// <summary>
    /// Calls <c>PATCH {organisationId}/{applicationId}/overseas-sites/{siteId}/recycling-operations</c>
    /// on the backend with <c>{ operationCodes: [...] }</c> as the body.
    /// </summary>
    Task<OverseasSiteRecyclingOperationsResult> UpdateRecyclingOperationsAsync(
        OverseasSiteRecyclingOperationsRequest request,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Everything <see cref="IOverseasSiteRecyclingOperationsAdapter.UpdateRecyclingOperationsAsync"/>
/// needs, bundled into one value rather than a long parameter list.
/// </summary>
/// <param name="OrganisationId">Backend organisation id — sourced from the
/// work item payload's operator organisation id.</param>
/// <param name="ApplicationId">Backend <c>AccreditationApplicationModel.Id</c>
/// — see <see cref="AccreditationNumberRequest.ApplicationId"/>'s doc
/// comment for the confirmed source of this value.</param>
/// <param name="SiteId">The overseas site's id on the accreditation
/// application.</param>
/// <param name="OperationCodes">The full replacement set of recycling
/// operation codes for this site (e.g. <c>R3</c>/<c>R4</c>/<c>R5</c>/
/// <c>R12</c>/<c>R13</c>) — this is a full replace, not a delta.</param>
/// <param name="UserId">The acting regulator's user id (from the
/// <c>user:id</c> claim) — forwarded so the backend's audit record
/// (AC15/AC19) attributes the edit to a real human, not a null/service
/// identity. Signed into the v3 HMAC payload alongside
/// <paramref name="UserName"/> — see <c>OperatorBackendSigning.AddHeaders</c>.</param>
/// <param name="UserName">The acting regulator's display name (from the
/// <c>user:name</c> claim).</param>
/// <param name="CorrelationId">One id per logical call, forwarded as the
/// <c>X-Correlation-Id</c> header — same cross-repo contract
/// <see cref="IAccreditationNumberAdapter"/> and
/// <see cref="IOperatorBackendPushAdapter"/> already use.</param>
public sealed record OverseasSiteRecyclingOperationsRequest(
    string OrganisationId,
    string ApplicationId,
    string SiteId,
    IReadOnlyList<string> OperationCodes,
    string? UserId,
    string? UserName,
    Guid CorrelationId
);

/// <summary>
/// Distinguishes the shapes of outcome the recycling-operations endpoint
/// (RA-469 3wc) needs to map onto distinct HTTP status codes.
/// </summary>
public enum OverseasSiteRecyclingOperationsOutcome
{
    Success,
    ValidationFailed,
    NotFound,
    Conflict,
    TransientFailure,
}

/// <summary>
/// Outcome of a <see cref="IOverseasSiteRecyclingOperationsAdapter"/> call.
/// Never thrown its way out of the adapter — the caller inspects
/// <see cref="Outcome"/> and maps it onto an HTTP response.
/// </summary>
public sealed record OverseasSiteRecyclingOperationsResult(
    OverseasSiteRecyclingOperationsOutcome Outcome,
    string? SiteJson,
    string? ErrorCode,
    string? Field,
    string? Message
)
{
    public bool IsSuccess => Outcome == OverseasSiteRecyclingOperationsOutcome.Success;

    public static OverseasSiteRecyclingOperationsResult Success(string siteJson) =>
        new(OverseasSiteRecyclingOperationsOutcome.Success, siteJson, null, null, null);

    /// <summary>
    /// The backend returned 400. <paramref name="errorCode"/>/<paramref name="field"/>
    /// are parsed from the backend's ProblemDetails-shaped body (the same
    /// <c>errorCode</c>/<c>field</c> extensions convention
    /// <c>DulyMake</c>/<c>LogDecision</c> use) when present, falling back to
    /// a generic <c>"validation-failed"</c> code when the body carries
    /// neither — a malformed/unparseable 400 body still surfaces as a
    /// validation failure rather than a TransientFailure.
    /// </summary>
    public static OverseasSiteRecyclingOperationsResult ValidationFailed(
        string? errorCode,
        string? field,
        string? detail
    ) =>
        new(
            OverseasSiteRecyclingOperationsOutcome.ValidationFailed,
            null,
            errorCode ?? "validation-failed",
            field,
            detail
        );

    public static OverseasSiteRecyclingOperationsResult NotFound() =>
        new(OverseasSiteRecyclingOperationsOutcome.NotFound, null, null, null, "Site not found.");

    public static OverseasSiteRecyclingOperationsResult Conflict(string message) =>
        new(OverseasSiteRecyclingOperationsOutcome.Conflict, null, null, null, message);

    public static OverseasSiteRecyclingOperationsResult TransientFailure(string message) =>
        new(OverseasSiteRecyclingOperationsOutcome.TransientFailure, null, null, null, message);
}
