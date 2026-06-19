namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Helpers for working with terminal work item states across every registered
/// type. Terminal states are the single source of truth for which items are
/// "done" and therefore archivable / hidden from the active worklist by default
/// (RA-224). The set is derived from <see cref="IWorkItemType.States"/> rather
/// than hardcoded, so adding a terminal state to any type automatically extends
/// the archive treatment.
/// </summary>
internal static class TerminalStates
{
    /// <summary>
    /// The distinct, case-insensitive set of terminal state ids declared by
    /// every registered work item type.
    /// </summary>
    internal static IReadOnlySet<string> Ids(IWorkItemRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return registry.Types
            .SelectMany(t => t.States)
            .Where(s => s.IsTerminal)
            .Select(s => s.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
