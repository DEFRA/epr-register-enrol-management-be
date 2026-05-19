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
    Breached
}
