using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.Integrations.OperatorBackend;

/// <summary>
/// RA-448 phase 2: resolves a real, backend-issued accreditation number for
/// a re-accreditation work item, replacing the locally-fabricated id
/// <c>AccreditationIdGenerator</c> used to produce.
///
/// Deliberately NOT part of <see cref="IOperatorBackendPushAdapter"/>: that
/// interface's contract is explicitly fire-and-forget ("a push failure must
/// not unwind the already-persisted transition"), which is the wrong shape
/// here — approval genuinely depends on the number this returns, so a
/// caller must be able to abandon the operation on failure rather than
/// silently proceed with nothing. Still never throws, mirroring
/// <c>PushDecisionStatusChangedAsync</c>'s "hard pre-commit gate reported as
/// a typed result" convention rather than an exception.
/// </summary>
public interface IAccreditationNumberAdapter
{
    /// <summary>
    /// Calls <c>POST {organisationId}/{applicationId}/accreditation-number</c>
    /// on the backend. The backend itself decides generate-vs-reapply based
    /// on whether it already has a stored accreditation number for this
    /// application — <see cref="AccreditationNumberRequest.Regenerate"/> only
    /// controls what happens when one already exists (reapply/YY+1 when
    /// true, unchanged idempotent return when false); a first-ever generate
    /// always happens regardless of this flag when the backend has no
    /// number yet.
    /// </summary>
    Task<AccreditationNumberResult> GenerateOrUpdateAccreditationNumberAsync(
        AccreditationNumberRequest request,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Everything <see cref="IAccreditationNumberAdapter.GenerateOrUpdateAccreditationNumberAsync"/>
/// needs, bundled into one value rather than a long parameter list.
/// </summary>
/// <param name="OrganisationId">Backend organisation id — sourced from the
/// work item payload's operator organisation id.</param>
/// <param name="ApplicationId">Backend <c>AccreditationApplicationModel.Id</c>
/// — confirmed (RA-448 phase 2 review, resolving AC11) against
/// <c>HttpCaseWorkingApiAdapter.BuildPayload</c> in epr-register-enrol-backend,
/// which sends <c>operatorApplicationId = application.ApplicationId</c> (that
/// model's own Mongo id, stringified) on every real submission. Callers
/// should source this from the work item payload's <c>operatorApplicationId</c>,
/// not <c>operatorRegistrationId</c> (a different value — the seed-time ReEx
/// registration id).</param>
/// <param name="Nation">Regulator nation; becomes the number's agency
/// letter.</param>
/// <param name="OrgId">The organisation's real numeric Org ID, or null when
/// this service does not have one. RA-475: null is the NORMAL case for a
/// genuinely-submitted application — the only organisation id this service
/// holds is <c>operatorOrganisationId</c>, which is the ReEx organisation id
/// and is a UUID. The backend resolves the numeric id from ReEx itself (see
/// <c>AccreditationApplicationEndpoints.ResolveOrgIdAsync</c>) and only falls
/// back to this value, which is why passing a parsed one is still worth doing
/// for the numeric seeded/stub organisations that have no ReEx document
/// behind them.</param>
/// <param name="Year">Four-digit accreditation year.</param>
/// <param name="Regenerate">See the interface method's doc comment.</param>
/// <param name="CorrelationId">One id per logical call, forwarded as the
/// <c>X-Correlation-Id</c> header, so this service's logs and the backend's
/// logs for the same request can be joined on one value — same cross-repo
/// contract <see cref="IOperatorBackendPushAdapter"/>'s pushes already
/// use.</param>
public sealed record AccreditationNumberRequest(
    string OrganisationId,
    string ApplicationId,
    Nation Nation,
    int? OrgId,
    int Year,
    bool Regenerate,
    Guid CorrelationId
);

/// <summary>
/// Outcome of a <see cref="IAccreditationNumberAdapter"/> call. Never thrown
/// its way out of the adapter — the caller inspects <see cref="IsSuccess"/>
/// and decides whether to abandon the operation.
/// </summary>
public sealed record AccreditationNumberResult(
    bool IsSuccess,
    string? AccreditationNumber,
    string? ErrorMessage
)
{
    public static AccreditationNumberResult Success(string accreditationNumber) =>
        new(true, accreditationNumber, null);

    public static AccreditationNumberResult Failure(string errorMessage) =>
        new(false, null, errorMessage);
}
