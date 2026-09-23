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

    /// <summary>
    /// The absolute determination deadline: <see cref="StartedAt"/> +
    /// <see cref="TargetDuration"/>.
    /// <para>
    /// RA-601 allows the deadline to be moved earlier, so
    /// <see cref="TargetDuration"/> may legitimately be zero or negative and
    /// the deadline may fall before <see cref="StartedAt"/>. The addition is
    /// therefore saturating rather than checked: a stored clock whose arithmetic
    /// would fall outside the representable <see cref="DateTime"/> range clamps
    /// to <see cref="DateTime.MinValue"/> / <see cref="DateTime.MaxValue"/>
    /// instead of throwing, so no single bad document can 500 every read of that
    /// work item. <c>SlaService.ExtendAsync</c> refuses to write such a value in
    /// the first place; this is defence in depth for data that predates it.
    /// </para>
    /// </summary>
    public DateTime DueAt
    {
        get
        {
            var ticks = unchecked(StartedAt.Ticks + TargetDurationTicks);
            if (((StartedAt.Ticks ^ ticks) & (TargetDurationTicks ^ ticks)) < 0)
            {
                // long overflow: the sign of the delta tells us which way.
                return TargetDurationTicks < 0 ? DateTime.MinValue : DateTime.MaxValue;
            }
            if (ticks < DateTime.MinValue.Ticks) return DateTime.MinValue;
            if (ticks > DateTime.MaxValue.Ticks) return DateTime.MaxValue;
            return new DateTime(ticks, StartedAt.Kind);
        }
    }

    /// <summary>
    /// Compute the remaining duration relative to <paramref name="now"/>.
    /// Negative once the deadline has passed — including immediately, when
    /// RA-601 has moved the deadline into the past.
    /// </summary>
    public TimeSpan Remaining(DateTime now) => DueAt - now;

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
