namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// RA-131: per-work-item Service-Level-Agreement clock. Captures when the
/// SLA window started, how long it is permitted to run for, and whether
/// the deadline has been breached. Nullable on <see cref="WorkItem"/> —
/// a missing clock means the SLA has not yet been started for that item
/// (e.g. submission has happened but the qualifying lifecycle event that
/// starts the clock has not).
///
/// Owned by the framework so every work item type inherits the same
/// extend / override semantics without writing any code. The companion
/// "started by" event (RA-130) lives outside this story; this file is
/// deliberately just the data shape and is mutated only via
/// <see cref="ISlaService"/> on the extend / override paths.
/// </summary>
public sealed class SlaClock
{
    /// <summary>UTC moment the SLA window began.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>
    /// Total time the window is permitted to run for (counted from
    /// <see cref="StartedAt"/>). Mutated by an "extend" call which adds
    /// to it, or by an "override" call which replaces it wholesale.
    /// </summary>
    public TimeSpan TargetDuration { get; set; }

    /// <summary>
    /// True once the clock has been observed to be past
    /// <see cref="StartedAt"/> + <see cref="TargetDuration"/> by the
    /// nightly breach job (RA-130). Stored on the document so list /
    /// dashboard reads do not need to recompute it.
    /// </summary>
    public bool Breached { get; set; }
}
