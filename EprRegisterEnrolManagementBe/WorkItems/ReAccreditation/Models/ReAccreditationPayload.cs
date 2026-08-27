using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

/// <summary>
/// Payload shape captured at the front-of-funnel ingestion of a
/// re-accreditation work item. Just enough fields to drive the
/// <see cref="IReAccreditationDecisionService"/> recommendation.
///
/// Stored on the work item envelope as a free-form BSON sub-document; this
/// record is the module's interpretation of that document, deserialised on
/// demand when the module needs to reason about the payload.
///
/// Marked <see cref="BsonIgnoreExtraElementsAttribute"/> so envelope-level
/// fields stamped onto the payload by the core <c>WorkItemService</c>
/// (e.g. <c>applicationReference</c>, <c>source</c>, <c>siteAddressLine1</c>)
/// do not cause deserialisation to fail when the module re-reads its
/// own slice of the payload.
/// </summary>
[BsonIgnoreExtraElements]
internal sealed record ReAccreditationPayload
{
    public string? OrganisationName { get; init; }
    public string? RegistrationNumber { get; init; }

    /// <summary>
    /// Human-facing application reference (RA-219, format RA-#########),
    /// stamped onto the payload by the core WorkItemService at submission.
    /// Surfaced as the ((reference)) GOV.UK Notify placeholder in lifecycle
    /// emails (RA-248) so operators see the case reference rather than the
    /// internal work-item Guid. Null only for legacy items predating RA-219.
    /// </summary>
    public string? ApplicationReference { get; init; }

    public string? Material { get; init; }

    /// <summary>
    /// RA-307: the glass recycling process, present only when
    /// <see cref="Material"/> is glass. Stamped onto the payload by the
    /// operator backend's HttpCaseWorkingApiAdapter as a plain string wire
    /// value (e.g. "glass_re_melt"); absent for every non-glass material and
    /// for glass applications that predate RA-307.
    ///
    /// BsonRepresentation(String) pins ToBsonDocument() to write the enum's
    /// member name rather than the driver's default ordinal int — the member
    /// names are the wire values themselves (see GlassRecyclingProcess.cs),
    /// so this keeps every write consistent with what ingestion already
    /// stores. Matches the convention on WorkItem/WorkItemAuditEntry/WorkItemNote.
    /// </summary>
    [BsonRepresentation(BsonType.String)]
    public GlassRecyclingProcess? GlassRecyclingProcess { get; init; }

    public int? PreviousAccreditationYear { get; init; }
    public int? ComplianceIssuesReported { get; init; }

    /// <summary>
    /// ReEx organisation identifier for the submitting operator. Used by the
    /// prior-year endpoint to look up accreditation data from ReEx. Populated
    /// by the operator backend at submission time; absent for work items created
    /// through the case management form.
    /// </summary>
    public string? OperatorOrganisationId { get; init; }

    /// <summary>
    /// ReEx registration identifier for the submitting operator. Used together
    /// with <see cref="OperatorOrganisationId"/> for prior-year lookups.
    /// </summary>
    public string? OperatorRegistrationId { get; init; }

    /// <summary>
    /// RA-448 phase 2: the operator backend's own <c>AccreditationApplicationModel.Id</c>
    /// — confirmed (not assumed) against <c>HttpCaseWorkingApiAdapter.BuildPayload</c>
    /// in epr-register-enrol-backend, which sends
    /// <c>operatorApplicationId = application.ApplicationId</c> (that model's
    /// Mongo <c>Id</c>, stringified) on every real submission. This, not
    /// <see cref="OperatorRegistrationId"/> (the seed-time ReEx registration id —
    /// a different value), is the correct <c>{applicationId}</c> route segment
    /// for the accreditation-number endpoint: every subsequent route on that
    /// backend's <c>accreditation-applications</c> group keys on the same
    /// document id, not on the registration id.
    /// </summary>
    public string? OperatorApplicationId { get; init; }

    /// <summary>
    /// Operator email address used as the GOV.UK Notify recipient for
    /// the lifecycle email templates wired up by
    /// <c>ReAccreditationNotificationHook</c> (RA-123). Optional —
    /// notifications are skipped (and recorded as such in the audit
    /// log) when missing.
    /// </summary>
    public string? OperatorEmail { get; init; }

    /// <summary>
    /// Postcode of the regulated site. Used by <c>ReAccreditationNationRoutingHook</c>
    /// (RA-125) to derive the <see cref="Nation"/> field via
    /// <see cref="INationResolver"/>. Optional — when absent the nation
    /// defaults to <see cref="Nation.England"/>.
    /// </summary>
    public string? SiteAddressPostcode { get; init; }

    /// <summary>
    /// The UK nation to which this application is routed, derived from
    /// <see cref="SiteAddressPostcode"/> by <c>ReAccreditationNationRoutingHook</c>
    /// (RA-125). Written by the server at submission time; callers should
    /// not include it in the submission payload.
    /// </summary>
    public Nation? Nation { get; init; }

    /// <summary>
    /// Issued accreditation identifier (RA-132). Generated by the
    /// approval service when a decision-maker approves the application.
    /// </summary>
    public string? AccreditationId { get; init; }

    /// <summary>
    /// Date the issued accreditation takes effect (RA-132). Stamped by
    /// the approval service at the moment of approval.
    /// </summary>
    public DateOnly? AccreditationStartDate { get; init; }

    /// <summary>
    /// Four-digit accreditation year stamped at approval time
    /// (RA-133). Sourced from the <c>Accreditation:CurrentYear</c>
    /// configuration setting and used to derive both the year segment
    /// of <see cref="AccreditationId"/> and the
    /// <see cref="AccreditationStartDate"/>.
    /// </summary>
    public int? AccreditationYear { get; init; }

    /// <summary>
    /// RA-132 SLA clock state. Set by the approval service when the
    /// clock is stopped on approval.
    /// </summary>
    public SlaClock? SlaClock { get; init; }

    /// <summary>
    /// RA-316: the registration charge for this application, in PENCE (minor
    /// units) — £3,276 is 327600. Supplied by the operator backend in the
    /// submission payload and displayed, read-only, on the duly-making page so
    /// the regulator can confirm the charge before recording the payment date.
    ///
    /// Nullable and never written by this service. Absent on every work item
    /// submitted before RA-316, so readers must degrade rather than assume a
    /// value; note that <c>0</c> is a legitimate charge and must not be
    /// conflated with "missing".
    /// </summary>
    public int? ChargeAmountPence { get; init; }

    /// <summary>
    /// RA-316/RA-503: the operator's payment reference, displayed read-only alongside
    /// <see cref="ChargeAmountPence"/> on the duly-making page.
    ///
    /// RA-503: genuinely operator-supplied on every real submission -
    /// epr-register-enrol-frontend computes the nation-specific bank reference
    /// (buildPaymentReference, e.g. <c>PR/PK/REP/500500</c>) and sends it in the submission
    /// payload; WorkItemService.SubmitAsync preserves it rather than overwriting it with the
    /// generated <see cref="ApplicationReference"/>. This is the exact string the operator's
    /// own "Payment details" page rendered under the literal label "Payment reference"
    /// alongside the sort code and account number - the reference they actually quoted on
    /// the bank transfer, now the same value the regulator sees here.
    ///
    /// Still nullable and still falls back to <see cref="ApplicationReference"/> when absent -
    /// a work item created before this fix, or by a submitter that doesn't send it.
    /// </summary>
    public string? PaymentReference { get; init; }

    /// <summary>
    /// RA-316: the payment date the regulator entered when completing duly
    /// making. Stamped by <c>ReAccreditationDulyMakingService</c> at that
    /// moment, and the value <see cref="Core.WorkItemSlaClock.StartedAt"/> is
    /// anchored to — the 12-week SLA runs from when payment was made, not from
    /// when the regulator got round to recording it.
    ///
    /// Null until duly making completes. Written by the server only; callers
    /// must not include it in the submission payload.
    /// </summary>
    public DateOnly? PaymentDate { get; init; }

    /// <summary>
    /// RA-291: the query currently open against this application, stamped by
    /// <c>ReAccreditationQueryService</c> just before the query transition so
    /// the notification hook can include the reason in the operator's email.
    /// See <see cref="Models.CurrentQuery"/> for why this is payload state and
    /// why readers must gate on the <c>queried</c> state.
    /// </summary>
    public CurrentQuery? CurrentQuery { get; init; }
}
