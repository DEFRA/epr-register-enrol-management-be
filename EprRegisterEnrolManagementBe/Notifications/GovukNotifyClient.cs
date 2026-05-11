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
/// </summary>
internal sealed class GovukNotifyClient : INotifyClient
{
    private readonly IAsyncNotificationClient _client;
    private readonly NotifyConfig _config;
    private readonly ILogger<GovukNotifyClient> _logger;
    private readonly ResiliencePipeline _retryPipeline;

    public GovukNotifyClient(
        IAsyncNotificationClient client,
        IOptions<NotifyConfig> options,
        ILogger<GovukNotifyClient> logger,
        ResiliencePipeline? retryPipeline = null)
    {
        _client = client;
        _config = options.Value;
        _logger = logger;
        _retryPipeline = retryPipeline ?? BuildRetryPipeline(logger);
    }

    public async Task<NotifySendResult> SendEmailAsync(
        string templateKey,
        string toEmail,
        Dictionary<string, string> personalisation,
        string reference,
        CancellationToken cancellationToken = default)
    {
        if (!_config.Templates.TryGetValue(templateKey, out var templateId)
            || string.IsNullOrWhiteSpace(templateId))
        {
            var error = $"No Notify template configured for key '{templateKey}'.";
            _logger.LogWarning("Notify send aborted: {Error}", error);
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

            return NotifySendResult.Success(response.id);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Notify send failed after retries: template={TemplateKey} to={Recipient} reference={Reference}",
                templateKey, toEmail, reference);
            return NotifySendResult.Failure(ex.Message);
        }
    }

    /// <summary>
    /// 3 attempts with exponential backoff (1s, 2s, 4s). Retries any
    /// exception thrown by the SDK — its <c>NotifyClientException</c>
    /// is the wrapper for both transport and API errors and there is
    /// no public sub-class to discriminate transient from terminal
    /// failures.
    /// </summary>
    private static ResiliencePipeline BuildRetryPipeline(ILogger logger) =>
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromSeconds(1),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(),
                OnRetry = args =>
                {
                    logger.LogWarning(args.Outcome.Exception,
                        "Notify send attempt {Attempt} failed; retrying after {Delay}",
                        args.AttemptNumber + 1, args.RetryDelay);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
}
