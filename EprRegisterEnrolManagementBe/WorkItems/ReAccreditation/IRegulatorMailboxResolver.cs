using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Resolves the regional regulator shared-mailbox address for the UK
/// <see cref="Nation"/> a re-accreditation application has been routed to
/// (RA-236). The nation is derived from the site address postcode by
/// <see cref="INationResolver"/>; mailbox addresses are configured per nation
/// under <c>Notify:RegulatorMailboxes</c>.
///
/// This is recipient-resolution infrastructure only — it does not send any
/// email. When a nation has no configured (non-blank) mailbox the resolver
/// returns <c>null</c> so downstream callers can skip the send and record a
/// <c>missing-regulator-mailbox</c> audit entry, mirroring the existing
/// missing-operator-email behaviour.
/// </summary>
public interface IRegulatorMailboxResolver
{
    /// <summary>
    /// Returns the configured shared-mailbox address for
    /// <paramref name="nation"/>, or <c>null</c> when
    /// <paramref name="nation"/> is null or its mailbox is unconfigured
    /// (missing or blank).
    /// </summary>
    string? Resolve(Nation? nation);
}
