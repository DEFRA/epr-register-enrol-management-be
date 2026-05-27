using System.Diagnostics;
using EprRegisterEnrolManagementBe.Utils.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notify.Interfaces;
using Polly;
using Polly.Retry;

namespace EprRegisterEnrolManagementBe.Notifications;

/// <summary>
/// Real <see cref="INotifyClient"/> implementation: wraps an
/// <see cref="IAsyncNotificationClient"/> from the GovukNotify SDK and
/// applies a 3-attempt exponential-backoff retry pipeline around the
/// remote send. Failures are surfaced as <see cref="NotifySendResult"/>
/// values rather than exceptions so the caller can record an audit
/// entry without unwinding the originating mutation.
///
/// The underlying transport is owned by GovukNotify (its
/// <c>NotificationClient</c> constructs and disposes its own
/// <see cref="System.Net.Http.HttpClient"/>) — a deliberate deviation
/// from the project-wide <c>AddHttpClientWithTracing</c> rule. The CDP
/// proxy is honoured via the <c>HTTP_PROXY</c> / <c>HTTPS_PROXY</c>
/// env vars because the SDK uses the default
/// <see cref="System.Net.Http.HttpClient"/> which respects them.
///
/// <para>
/// Diagnostic logging goes through <see cref="IStructuredLogger{T}"/>
/// with the dotted-ECS shape (<c>event.category=notify</c>,
/// <c>event.action=send_email</c>, <c>event.outcome</c>,
/// <c>event.reason</c>, <c>event.duration</c>, <c>event.reference</c>)
/// so failed sends can be queried in OpenSearch on CDP — see
/// <c>docs/cdp-observability.md</c>.
/// </para>
/// </summary>
internal sealed class GovukNotifyClient : INotifyClient
{
    internal const string EventCategory = "notify";
    internal const string EventAction = "send_email";

    private readonly IAsyncNotificationClient _client;
    private readonly NotifyConfig _config;
    private readonly IStructuredLogger<GovukNotifyClient> _log;
    private readonly ResiliencePipeline _retryPipeline;

    public GovukNotifyClient(
        IAsyncNotificationClient client,
        IOptions<NotifyConfig> options,
        IStructuredLogger<GovukNotifyClient> log,
        ResiliencePipeline? retryPipeline = null)
    {
        _client = client;
        _config = options.Value;
        _log = log;
        _retryPipeline = retryPipeline ?? BuildRetryPipeline(log);
    }

    public async Task<NotifySendResult> SendEmailAsync(
        string templateKey,
        string toEmail,
        Dictionary<string, string> personalisation,
        string reference,
        CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();

        if (!_config.Templates.TryGetValue(templateKey, out var templateId)
            || string.IsNullOrWhiteSpace(templateId))
        {
            var error = $"No Notify template configured for key '{templateKey}'.";
            // Misconfiguration: emit a failure entry so it shows up
            // alongside transport failures in the dashboard.
            _log.Log(
                LogLevel.Error,
                "Notify send aborted: template not configured",
                BuildProperties(
                    outcome: "failure",
                    duration: timer.Elapsed,
                    reference: reference,
                    reason: "template_not_configured",
                    extras: new Dictionary<string, object?>
                    {
                        ["notify.template_key"] = templateKey
                    }));
            return NotifySendResult.Failure(error);
        }

        // GovukNotify's API takes Dictionary<string, dynamic>. Project
        // strings into that shape — we never need to send richer types.
        var typedPersonalisation = personalisation.ToDictionary(
            kv => kv.Key,
            kv => (dynamic)kv.Value);

        try
        {
            var response = await _retryPipeline.ExecuteAsync(
                async ct => await _client.SendEmailAsync(
                    toEmail,
                    templateId,
                    typedPersonalisation,
                    reference).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);

            _log.Log(
                LogLevel.Information,
                "Notify send succeeded",
                BuildProperties(
                    outcome: "success",
                    duration: timer.Elapsed,
                    reference: reference));

            return NotifySendResult.Success(response.id);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Final, post-retry failure. Attach the exception so ECS
            // error.* fields carry the SDK's diagnostic detail.
            _log.Log(
                LogLevel.Error,
                "Notify send failed after retries",
                BuildProperties(
                    outcome: "failure",
                    duration: timer.Elapsed,
                    reference: reference,
                    reason: "send_failed_after_retries",
                    extras: new Dictionary<string, object?>
                    {
                        ["notify.template_key"] = templateKey
                    }),
                exception: ex);
            return NotifySendResult.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Build the common dotted-ECS property bag for a Notify log
    /// entry. <paramref name="duration"/> is converted to nanoseconds
    /// per the ECS <c>event.duration</c> convention (1 TimeSpan tick
    /// == 100ns, so we preserve Stopwatch precision).
    /// </summary>
    private static IReadOnlyDictionary<string, object?> BuildProperties(
        string outcome,
        TimeSpan duration,
        string? reference,
        string? reason = null,
        IReadOnlyDictionary<string, object?>? extras = null)
    {
        var props = new Dictionary<string, object?>
        {
            ["event.category"] = EventCategory,
            ["event.action"] = EventAction,
            ["event.outcome"] = outcome,
            ["event.duration"] = duration.Ticks * 100,
            ["event.reference"] = reference
        };
        if (reason is not null)
        {
            props["event.reason"] = reason;
        }
        if (extras is not null)
        {
            foreach (var (k, v) in extras)
            {
                props[k] = v;
            }
        }
        return props;
    }

    /// <summary>
    /// 3 attempts with exponential backoff (1s, 2s, 4s). Retries any
    /// exception thrown by the SDK — its <c>NotifyClientException</c>
    /// is the wrapper for both transport and API errors and there is
    /// no public sub-class to discriminate transient from terminal
    /// failures.
    /// </summary>
    private static ResiliencePipeline BuildRetryPipeline(
        IStructuredLogger<GovukNotifyClient> log) =>
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromSeconds(1),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(),
                OnRetry = args =>
                {
                    log.Log(
                        LogLevel.Warning,
                        "Notify send attempt failed; will retry",
                        new Dictionary<string, object?>
                        {
                            ["event.category"] = EventCategory,
                            ["event.action"] = EventAction,
                            ["event.outcome"] = "failure",
                            ["event.attempt"] = args.AttemptNumber + 1,
                            ["notify.retry_delay_ms"] = (long)args.RetryDelay.TotalMilliseconds
                        },
                        exception: args.Outcome.Exception);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
}

