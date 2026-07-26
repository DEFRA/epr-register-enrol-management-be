namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

/// <summary>
/// RA-294/RA-297 request body for
/// <c>POST /work-items/re-accreditation/{id}/site-added</c>. Posted by the
/// operator backend whenever an operator adds a new Overseas Reprocessing
/// Site (ORS) or a new interim site (a waste staging point linked 1:1 to an
/// ORS) to an accreditation application, so the regulator's work item picks
/// up an audit trail entry for it.
///
/// This repo never models ORS/interim-site detail itself — <see cref="Core.WorkItem.Payload"/>
/// stays schemaless BSON — so every field here is recorded verbatim as audit
/// details and nothing else. <see cref="SiteType"/> is nullable so a
/// structurally malformed body reaches <see cref="ReAccreditationSiteAddedValidator"/>
/// and is rejected with a ProblemDetails 400 rather than a binding failure.
/// </summary>
internal sealed record SiteAddedRequest(
    string? SiteType,
    string? OrsId,
    string? SiteNumber,
    bool IsNewSite
);
