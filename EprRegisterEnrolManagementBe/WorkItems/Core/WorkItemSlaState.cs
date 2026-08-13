using System.Text.Json.Serialization;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>SLA state derived from <see cref="WorkItemSlaClock"/> at read time.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkItemSlaState>))]
public enum WorkItemSlaState
{
    /// <summary>More than 14 days remain on the SLA clock.</summary>
    OnTrack,

    /// <summary>14 days or fewer remain, but the deadline has not passed.</summary>
    AtRisk,

    /// <summary>The deadline has passed or <see cref="WorkItemSlaClock.Breached"/> is true.</summary>
    Breached,

    /// <summary>
    /// The work item has reached a terminal state (withdrawn / approved /
    /// rejected), so its SLA clock is cancelled and no longer running (RA-359).
    /// This is a read-time projection: the historical due date is preserved on
    /// the persisted clock; only the derived running state is neutralised.
    /// </summary>
    Cancelled
}
