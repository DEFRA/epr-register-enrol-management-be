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
    /// application — <paramref name="regenerate"/> only controls what
    /// happens when one already exists (reapply/YY+1 when true, unchanged
    /// idempotent return when false); a first-ever generate always happens
    /// regardless of this flag when the backend has no number yet.
    /// </summary>
    /// <param name="organisationId">Backend organisation id — sourced from
    /// the work item payload's operator organisation id.</param>
    /// <param name="applicationId">Backend AccreditationApplicationModel id.
    /// Callers should treat this as a verified-during-implementation
    /// assumption, not a guaranteed-correct value, until confirmed against
    /// real data — see the Phase 2 doc's AC11.</param>
    /// <param name="nation">Regulator nation; becomes the number's agency
    /// letter.</param>
    /// <param name="orgId">The organisation's real numeric Org ID.</param>
    /// <param name="year">Four-digit accreditation year.</param>
    /// <param name="regenerate">See summary above.</param>
    /// <param name="correlationId">One id per logical call, forwarded as the
    /// <c>X-Correlation-Id</c> header, so this service's logs and the
    /// backend's logs for the same request can be joined on one value —
    /// same cross-repo contract <see cref="IOperatorBackendPushAdapter"/>'s
    /// pushes already use.</param>
    Task<AccreditationNumberResult> GenerateOrUpdateAccreditationNumberAsync(
        string organisationId,
        string applicationId,
        Nation nation,
        int orgId,
        int year,
        bool regenerate,
        Guid correlationId,
        CancellationToken cancellationToken = default
    );
}

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
