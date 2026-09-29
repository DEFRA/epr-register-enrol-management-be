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
    /// RA-601 allowed the deadline to be moved earlier without limit, so
    /// <see cref="TargetDuration"/> may be zero or deeply negative on stored
    /// data and the deadline may fall days or years before
    /// <see cref="StartedAt"/>. RA-611 put a floor on
    /// <c>SlaService.ExtendAsync</c> — the deadline may not fall, as a UK
    /// calendar date, before the later of <see cref="StartedAt"/>'s own date and
    /// 1 January of the accreditation year — so an extend can no longer write a
    /// deadline on an earlier DATE than the clock start. It can still write a
    /// non-positive <see cref="TargetDuration"/>, because the floor compares
    /// dates rather than instants: a deadline landing on the start date itself
    /// but at an earlier time of day yields zero or a small negative (up to an
    /// hour under BST for a midnight-UTC start, more for a clock whose start was
    /// moved to mid-afternoon by <c>OverrideAsync</c>). <c>OverrideAsync</c>
    /// itself requires a positive target but can move <see cref="StartedAt"/>
    /// forward past an existing deadline. Documents written before RA-611 can
    /// hold anything at all, so every read path still has to tolerate it. The
    /// addition is
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
    /// Negative once the deadline has passed — whether because the deadline
    /// simply lapsed or because RA-611 allowed a caseworker to backdate it
    /// deliberately (as far back as the duly-made date).
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
