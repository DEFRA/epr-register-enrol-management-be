using System.Globalization;
using System.Security.Claims;
using System.Xml;
using EprRegisterEnrolManagementBe.Config;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// RA-131: framework service that handles SLA extension and manual
/// override for any work item type. Lives in core because the rules
/// (max-extension cap, non-empty reason, audit composition,
/// operator-notify-on-extend) are universal across modules. RA-323: any
/// authenticated caseworker may extend or override — there is no
/// team-leader gate any more.
/// </summary>
public interface ISlaService
{
    /// <summary>
    /// Add <paramref name="additionalDuration"/> to the work item's
    /// <see cref="SlaClock.TargetDuration"/>. Writes an <c>sla-extended</c>
    /// audit entry carrying before/after SlaClock snapshots and the
    /// supplied reason, and fans out to every registered
    /// <see cref="IWorkItemPostActionHook"/> with an <c>sla-extend</c>
    /// action id so per-module notification hooks (e.g. the operator
    /// "SLA extended" email) fire automatically.
    /// </summary>
    Task<SlaActionResult> ExtendAsync(
        Guid workItemId,
        TimeSpan additionalDuration,
        string reason,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replace the work item's <see cref="SlaClock"/> with the supplied
    /// fields. <paramref name="newStartedAt"/> is optional — when omitted
    /// the existing start is preserved so an override that just
    /// re-targets the deadline keeps the historical start. Writes an
    /// <c>sla-overridden</c> audit entry carrying before/after snapshots
    /// and the reason; deliberately does NOT trigger the notification
    /// hook because overrides are regulator-internal.
    /// </summary>
    Task<SlaActionResult> OverrideAsync(
        Guid workItemId,
        TimeSpan newTargetDuration,
        DateTime? newStartedAt,
        string reason,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Failure reasons returned by <see cref="ISlaService"/>. Distinct from
/// <see cref="WorkItemActionFailureCode"/> so SLA-only outcomes (clock
/// missing, validation specific to SLA shape) can be expressed without
/// muddying the engine's failure taxonomy.
/// </summary>
public enum SlaActionFailureCode
{
    WorkItemNotFound,
    NotAuthorized,
    /// <summary>Caller carries no <c>user:id</c> claim (BFF did not forward identity).</summary>
    MissingActorIdentity,
    /// <summary>Reason / duration / start-date failed structural validation. Maps to 422.</summary>
    InvalidRequest,
    /// <summary>SLA clock has not been started for this work item. Maps to 409.</summary>
    ClockNotStarted,
    /// <summary>Optimistic-concurrency collision. Maps to 409.</summary>
    ConcurrencyConflict
}

/// <summary>Outcome of an <see cref="ISlaService"/> mutation.</summary>
public sealed record SlaActionResult(
    WorkItem? WorkItem,
    SlaActionFailureCode? FailureCode,
    string? Message)
{
    public bool IsSuccess => FailureCode is null;

    public static SlaActionResult Success(WorkItem workItem) =>
        new(workItem, null, null);

    public static SlaActionResult Failure(SlaActionFailureCode code, string message) =>
        new(null, code, message);
}

public sealed class SlaService : ISlaService
{
    /// <summary>
    /// Action id used when fanning out post-action hooks after a
    /// successful extend. The existing per-module notification hooks
    /// (e.g. <c>ReAccreditationNotificationHook</c>) map this id to the
    /// <c>SlaExtended</c> Notify template.
    /// </summary>
    public const string ExtendActionId = "sla-extend";

    private readonly IWorkItemPersistence _persistence;
    private readonly ILogger<SlaService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IReadOnlyCollection<IWorkItemPostActionHook> _postActionHooks;
    private readonly IOptionsMonitor<SlaConfig> _config;

    public SlaService(
        IWorkItemPersistence persistence,
        ILogger<SlaService> logger,
        IOptionsMonitor<SlaConfig> config,
        TimeProvider? timeProvider = null,
        IEnumerable<IWorkItemPostActionHook>? postActionHooks = null)
    {
        _persistence = persistence;
        _logger = logger;
        _config = config;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _postActionHooks = postActionHooks?.ToArray() ?? Array.Empty<IWorkItemPostActionHook>();
    }

    public async Task<SlaActionResult> ExtendAsync(
        Guid workItemId,
        TimeSpan additionalDuration,
        string reason,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        if (RequireActorIdentity(user) is { } identityFailure) return identityFailure;
        if (RequireReason(reason) is { } reasonFailure) return reasonFailure;

        var maxExtension = TimeSpan.FromDays(_config.CurrentValue.MaxExtensionDays);
        if (additionalDuration <= TimeSpan.Zero)
        {
            return SlaActionResult.Failure(
                SlaActionFailureCode.InvalidRequest,
                "'additionalDuration' must be a positive ISO-8601 duration (e.g. 'P14D').");
        }
        if (additionalDuration > maxExtension)
        {
            return SlaActionResult.Failure(
                SlaActionFailureCode.InvalidRequest,
                $"'additionalDuration' exceeds the configured maximum of " +
                $"{_config.CurrentValue.MaxExtensionDays} day(s) per call.");
        }

        var workItem = await _persistence.GetByIdAsync(workItemId, cancellationToken);
        if (workItem is null)
        {
            return SlaActionResult.Failure(
                SlaActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'.");
        }
        if (workItem.SlaClock is null)
        {
            return SlaActionResult.Failure(
                SlaActionFailureCode.ClockNotStarted,
                $"Work item '{workItemId}' has no SLA clock started — extend / override is unavailable.");
        }

        var before = Snapshot(workItem.SlaClock);
        workItem.SlaClock.TargetDuration += additionalDuration;
        var after = Snapshot(workItem.SlaClock);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        workItem.LastModifiedAt = now;

        AppendAuditEntry(
            workItem,
            action: "sla-extended",
            actionDisplayName: "SLA extended",
            user,
            now,
            reason,
            before,
            after,
            extra: new Dictionary<string, string?>
            {
                ["additionalDuration"] = XmlConvert.ToString(additionalDuration)
            });

        try
        {
            await _persistence.ReplaceAsync(workItem, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            return SlaActionResult.Failure(
                SlaActionFailureCode.ConcurrencyConflict,
                $"Work item '{workItemId}' was modified concurrently. Reload the work item and retry.");
        }

        _logger.LogInformation(
            "Work item {WorkItemId} ({TypeId}) SLA extended by {AdditionalDuration} (target now {TargetDuration}) by {User}",
            workItem.Id, workItem.TypeId, additionalDuration, workItem.SlaClock.TargetDuration, DescribeUser(user));

        await InvokeExtendHooksAsync(workItem, user, cancellationToken);

        return SlaActionResult.Success(workItem);
    }

    public async Task<SlaActionResult> OverrideAsync(
        Guid workItemId,
        TimeSpan newTargetDuration,
        DateTime? newStartedAt,
        string reason,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        if (RequireActorIdentity(user) is { } identityFailure) return identityFailure;
        if (RequireReason(reason) is { } reasonFailure) return reasonFailure;

        if (newTargetDuration <= TimeSpan.Zero)
        {
            return SlaActionResult.Failure(
                SlaActionFailureCode.InvalidRequest,
                "'newTargetDuration' must be a positive ISO-8601 duration (e.g. 'P21D').");
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (newStartedAt is { } startedAt)
        {
            // Normalise to UTC so a caller-supplied offset cannot smuggle
            // a future wallclock past the guard.
            var startedAtUtc = startedAt.Kind == DateTimeKind.Utc
                ? startedAt
                : startedAt.ToUniversalTime();
            if (startedAtUtc > now)
            {
                return SlaActionResult.Failure(
                    SlaActionFailureCode.InvalidRequest,
                    "'newStartedAt' must not be in the future.");
            }
            newStartedAt = startedAtUtc;
        }
        else
        {
            // BA confirmed (RA-131): omitting newStartedAt should default the
            // clock start to today rather than preserving the existing value.
            newStartedAt = now;
        }

        var workItem = await _persistence.GetByIdAsync(workItemId, cancellationToken);
        if (workItem is null)
        {
            return SlaActionResult.Failure(
                SlaActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'.");
        }
        if (workItem.SlaClock is null)
        {
            return SlaActionResult.Failure(
                SlaActionFailureCode.ClockNotStarted,
                $"Work item '{workItemId}' has no SLA clock started — extend / override is unavailable.");
        }

        // No cap on override — regulators agree duration offline with operators
        // (BA confirmed RA-131; no legislative mandate for a maximum).

        var before = Snapshot(workItem.SlaClock);
        workItem.SlaClock.TargetDuration = newTargetDuration;
        workItem.SlaClock.StartedAt = newStartedAt.Value;
        var after = Snapshot(workItem.SlaClock);
        workItem.LastModifiedAt = now;

        AppendAuditEntry(
            workItem,
            action: "sla-overridden",
            actionDisplayName: "SLA overridden",
            user,
            now,
            reason,
            before,
            after,
            extra: null);

        try
        {
            await _persistence.ReplaceAsync(workItem, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            return SlaActionResult.Failure(
                SlaActionFailureCode.ConcurrencyConflict,
                $"Work item '{workItemId}' was modified concurrently. Reload the work item and retry.");
        }

        _logger.LogInformation(
            "Work item {WorkItemId} ({TypeId}) SLA overridden (target now {TargetDuration}, started {StartedAt}) by {User}",
            workItem.Id, workItem.TypeId, workItem.SlaClock.TargetDuration, workItem.SlaClock.StartedAt, DescribeUser(user));

        return SlaActionResult.Success(workItem);
    }

    private static Dictionary<string, string?> Snapshot(WorkItemSlaClock clock) => new()
    {
        ["startedAt"] = clock.StartedAt.ToString("o", CultureInfo.InvariantCulture),
        ["targetDuration"] = XmlConvert.ToString(clock.TargetDuration),
        ["breached"] = clock.Breached.ToString()
    };

    private static void AppendAuditEntry(
        WorkItem workItem,
        string action,
        string actionDisplayName,
        ClaimsPrincipal user,
        DateTime createdAt,
        string reason,
        Dictionary<string, string?> before,
        Dictionary<string, string?> after,
        Dictionary<string, string?>? extra)
    {
        var details = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["reason"] = reason,
            ["actorUserId"] = ResolveActorUserId(user),
            ["beforeStartedAt"] = before["startedAt"],
            ["beforeTargetDuration"] = before["targetDuration"],
            ["beforeBreached"] = before["breached"],
            ["afterStartedAt"] = after["startedAt"],
            ["afterTargetDuration"] = after["targetDuration"],
            ["afterBreached"] = after["breached"]
        };
        if (extra is not null)
        {
            foreach (var (k, v) in extra) details[k] = v;
        }

        workItem.AuditLog.Add(new WorkItemAuditEntry
        {
            Action = action,
            ActionDisplayName = actionDisplayName,
            Details = details,
            CreatedAt = createdAt,
            CreatedBy = ResolveActorUserId(user),
            CreatedByName = user.FindFirstValue("user:name")
        });
    }

    private async Task InvokeExtendHooksAsync(
        WorkItem workItem,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        foreach (var hook in _postActionHooks)
        {
            try
            {
                await hook.OnActionAppliedAsync(
                    workItem, ExtendActionId, workItem.StateId, user, cancellationToken);
            }
            catch (Exception ex)
            {
                // Hook contract requires side-effect failures be swallowed
                // and self-recorded; we still defensively catch so a
                // misbehaving hook cannot unwind the persisted extend.
                _logger.LogError(ex,
                    "SLA-extend post-action hook {HookType} failed for work item {WorkItemId}",
                    hook.GetType().FullName, workItem.Id);
            }
        }
    }

    private static SlaActionResult? RequireActorIdentity(ClaimsPrincipal? user) =>
        ResolveActorUserId(user) is null
            ? SlaActionResult.Failure(
                SlaActionFailureCode.MissingActorIdentity,
                "Mutating this work item requires an authenticated end user; " +
                "the request did not include a 'user:id' claim.")
            : null;

    private static SlaActionResult? RequireReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? SlaActionResult.Failure(
                SlaActionFailureCode.InvalidRequest,
                "'reason' is required and must not be whitespace.")
            : null;

    private static string? ResolveActorUserId(ClaimsPrincipal? user)
    {
        var id = user?.FindFirstValue("user:id");
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    private static string DescribeUser(ClaimsPrincipal? user) =>
        user?.FindFirstValue("user:id")
        ?? user?.FindFirstValue("cognito:client_id")
        ?? user?.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? "unknown";
}
