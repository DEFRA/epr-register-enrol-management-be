using System.Text.Json;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

/// <summary>
/// RA-311/MBE-1 request body for
/// <c>POST /work-items/re-accreditation/{id}/resume-from-query</c>, called
/// by the operator backend once an operator has resubmitted a queried
/// application.
///
/// <see cref="Sections"/> and <see cref="FileReferences"/> are deliberately
/// opaque/flat here: this repo has no model for the operator's own section
/// schemas (e.g. sampling-plan files, BES evidence uploads) — those are
/// owned by the operator backend, which extracts the current values and
/// file list itself before calling this endpoint. All members are nullable
/// so a structurally-broken body reaches <see cref="ReAccreditationResumeValidator"/>
/// and is rejected with a ProblemDetails 400 rather than a binding failure.
///
/// RA-766: <see cref="ChargeAmountPence"/> is the registration charge the
/// operator backend recalculated for the resubmitted application, in PENCE
/// (the operator backend is the only place the charge is calculated). Null
/// means "not sent" (an older operator backend, or no band) and leaves the
/// stored charge as it is.
/// </summary>
internal sealed record ResumeFromQueryRequest(
    ResponderContactDetails? ResponderContactDetails,
    IReadOnlyList<string>? SectionKeys,
    Dictionary<string, JsonElement>? Sections,
    IReadOnlyList<SectionFileReference>? FileReferences,
    int? ChargeAmountPence = null);

/// <summary>
/// Contact details for whoever resubmitted the application, captured on the
/// operator-facing query-declaration page (RA-311 §3) rather than defaulted
/// from a logged-in profile.
/// </summary>
internal sealed record ResponderContactDetails(string? FullName, string? Email, string? Role);

/// <summary>
/// A single file link surfaced in the AC08 audit trail. <see cref="SectionKey"/>
/// is one of the closed six-key set in <see cref="ReAccreditationQuerySections"/>.
/// </summary>
internal sealed record SectionFileReference(string? SectionKey, string? FileId, string? Filename, string? S3Key);
