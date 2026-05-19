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
    /// Target duration as a <see cref="TimeSpan"/> computed from the
    /// persisted <see cref="TargetDurationTicks"/> (Int64) so BSON
    /// serialization does not require a custom serializer for TimeSpan.
    /// </summary>
    [BsonIgnore]
    public TimeSpan TargetDuration => TimeSpan.FromTicks(TargetDurationTicks);

    /// <summary>Backing field for <see cref="TargetDuration"/>. Stored as ticks (Int64).</summary>
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
