namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

/// <summary>
/// RA-291: the query currently open against a re-accreditation application.
///
/// The business rule is one open query at a time, so "the current query" is
/// real domain state rather than a transient smuggled between components —
/// which is why it lives on the payload rather than being passed sideways.
/// It is stamped by <c>ReAccreditationQueryService</c> immediately BEFORE the
/// query transition is applied, so that
/// <c>ReAccreditationNotificationHook</c> — which runs as a post-action hook
/// INSIDE <c>ApplyActionAsync</c> — can read the reason it must put in the
/// operator's email. The same record is what the audit entry is built from,
/// so the reason in the email is by construction the reason recorded against
/// the application.
///
/// Only meaningful while the work item is in the <c>queried</c> state.
/// Because it is written just before the transition, a subsequent failure of
/// that transition can leave a stamped query on a non-queried item; readers
/// must therefore gate on <c>StateId == "queried"</c> rather than on the mere
/// presence of this record. It is deliberately not cleared afterwards — the
/// next query overwrites it, and the audit log remains the historical record.
/// </summary>
internal sealed record CurrentQuery
{
    /// <summary>Free-text reason, as entered by the case worker (trimmed).</summary>
    public string? Reason { get; init; }

    /// <summary>Section ids the query was raised against.</summary>
    public IReadOnlyList<string>? Sections { get; init; }

    public DateTime? RaisedAt { get; init; }

    /// <summary><c>user:id</c> of the case worker who raised the query.</summary>
    public string? RaisedBy { get; init; }
}
