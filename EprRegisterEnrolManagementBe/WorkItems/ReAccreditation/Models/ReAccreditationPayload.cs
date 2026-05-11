namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

/// <summary>
/// Payload shape captured at the front-of-funnel ingestion of a
/// re-accreditation work item. Just enough fields to drive the
/// <see cref="IReAccreditationDecisionService"/> recommendation.
///
/// Stored on the work item envelope as a free-form BSON sub-document; this
/// record is the module's interpretation of that document, deserialised on
/// demand when the module needs to reason about the payload.
/// </summary>
internal sealed record ReAccreditationPayload
{
    public string? OrganisationName { get; init; }
    public string? RegistrationNumber { get; init; }
    public List<string>? MaterialsHandled { get; init; }
    public int? PreviousAccreditationYear { get; init; }
    public int? ComplianceIssuesReported { get; init; }

    /// <summary>
    /// Operator email address used as the GOV.UK Notify recipient for
    /// the lifecycle email templates wired up by
    /// <c>ReAccreditationNotificationHook</c> (RA-123). Optional —
    /// notifications are skipped (and recorded as such in the audit
    /// log) when missing.
    /// </summary>
    public string? OperatorEmail { get; init; }
}