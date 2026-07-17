using EprRegisterEnrolManagementBe.Notifications;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Default <see cref="IRegulatorMailboxResolver"/>. Looks up the per-nation
/// shared-mailbox address from <see cref="NotifyConfig.RegulatorMailboxes"/>
/// (configured under <c>Notify:RegulatorMailboxes</c>), keyed by the
/// <see cref="Nation"/> enum name.
///
/// Lookup is case-insensitive (the underlying dictionary is built with
/// <see cref="StringComparer.OrdinalIgnoreCase"/>). A null nation, a missing
/// key, or a blank/whitespace value all resolve to <c>null</c>.
/// </summary>
internal sealed class RegulatorMailboxResolver(IOptions<NotifyConfig> options) : IRegulatorMailboxResolver
{
    private readonly NotifyConfig _config = options.Value;

    /// <inheritdoc />
    public string? Resolve(Nation? nation)
    {
        if (nation is null)
        {
            return null;
        }

        if (!_config.RegulatorMailboxes.TryGetValue(nation.Value.ToString(), out var mailbox))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(mailbox) ? null : mailbox;
    }
}
