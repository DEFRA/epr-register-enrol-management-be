using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// SLA clock stamped onto a work item when the operator completes payment.
/// <see cref="StartedAt"/> is set to the operator's <c>paidAt</c> (UTC,
/// server-validated). <see cref="TargetDuration"/> defaults to 12 weeks.
/// <see cref="Breached"/> is flipped to <c>true</c> by the nightly
/// background job once the deadline has passed (one write per item).
/// </summary>
public sealed class WorkItemSlaClock
{
    public DateTime StartedAt { get; set; }

    /// <summary>
    /// Target duration for the SLA window. Stored as ticks (<see cref="TargetDurationTicks"/>)
    /// in BSON so MongoDB does not need a custom TimeSpan serializer; this
    /// property is the ergonomic façade used by all application code.
    /// Defaults to 84 days (12 weeks). Mutable so <see cref="ISlaService"/>
    /// can extend or override the window without replacing the whole clock.
    /// </summary>
    [BsonIgnore]
    public TimeSpan TargetDuration
    {
        get => TimeSpan.FromTicks(TargetDurationTicks);
        set => TargetDurationTicks = value.Ticks;
    }

    /// <summary>Backing store for <see cref="TargetDuration"/>. Written to MongoDB as a plain Int64.</summary>
    [BsonElement("targetDuration")]
    public long TargetDurationTicks { get; set; } = TimeSpan.FromDays(84).Ticks; // 12 weeks

    public bool Breached { get; set; }

    /// <summary>Compute the remaining duration relative to <paramref name="now"/>.</summary>
    public TimeSpan Remaining(DateTime now) =>
        StartedAt + TargetDuration - now;

    /// <summary>
    /// Derive the SLA state from the current clock and <paramref name="now"/>.
    /// Items are <see cref="WorkItemSlaState.AtRisk"/> when fewer than 14 days
    /// remain (but not yet <see cref="WorkItemSlaState.Breached"/>).
    /// </summary>
    public WorkItemSlaState ComputeState(DateTime now)
    {
        if (Breached || Remaining(now) <= TimeSpan.Zero)
        {
            return WorkItemSlaState.Breached;
        }
        if (Remaining(now) <= TimeSpan.FromDays(14))
        {
            return WorkItemSlaState.AtRisk;
        }
        return WorkItemSlaState.OnTrack;
    }
}
