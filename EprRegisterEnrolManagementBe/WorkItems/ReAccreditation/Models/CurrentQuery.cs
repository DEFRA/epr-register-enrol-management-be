namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

/// <summary>
/// RA-291: the query currently open against a re-accreditation application.
///
/// The business rule is one open query at a time, so "the current query" is
/// real domain state rather than a transient smuggled between components —
/// which is why it lives on the payload rather than being passed sideways.
/// <c>ReAccreditationQueryService</c> writes it in the same versioned save as
/// the query transition (RA-000), so the post-action hooks that run inside
/// <c>ApplyActionAsync</c> read the very record that transition committed:
/// the query push sends its reason and sections to the operator backend, and
/// <c>ReAccreditationNotificationHook</c>'s Queried email uses only
/// <see cref="RaisedAt"/> (RA-581 removed the reason from that template). The
/// same record is what the <c>application-queried</c> audit entry is built
/// from, so the pushed reason is by construction the reason recorded against
/// the application.
///
/// Only meaningful while the work item is in the <c>queried</c> state. It is
/// deliberately not cleared afterwards — the next query overwrites it, and
/// the audit log remains the historical record. Readers must therefore not
/// trust its mere presence: the query-transition hooks check, via
/// <c>ReAccreditationFreshQuery</c>, that its <see cref="QueryId"/> is the one
/// recorded on the transition they are handling.
/// </summary>
internal sealed record CurrentQuery
{
    /// <summary>
    /// RA-000: identifies this query. The query transition's
    /// <c>action-applied</c> audit entry records the same value under
    /// <c>queryId</c>. Stamps written before RA-000 have none.
    /// </summary>
    public string? QueryId { get; init; }

    /// <summary>Free-text reason, as entered by the case worker (trimmed).</summary>
    public string? Reason { get; init; }

    /// <summary>Section ids the query was raised against.</summary>
    public IReadOnlyList<string>? Sections { get; init; }

    public DateTime? RaisedAt { get; init; }

    /// <summary><c>user:id</c> of the case worker who raised the query.</summary>
    public string? RaisedBy { get; init; }
}
