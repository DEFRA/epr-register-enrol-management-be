using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace EprRegisterEnrolManagementBe.Integrations.OperatorBackend;

/// <summary>
/// Shared Polly retry/timeout pipeline for operator-backend adapters — the
/// same shape <see cref="HttpAccreditationNumberAdapter"/> and
/// <see cref="HttpOverseasSiteRecyclingOperationsAdapter"/> both need
/// (jittered exponential backoff, a per-attempt timeout, retry on transport
/// exceptions/timeouts/5xx only, never a 4xx), factored out once both
/// classes' own copies were flagged as duplicated code. Each caller still
/// owns its own retry budget constants and doc comment explaining that
/// budget — only the pipeline construction and log message shape are
/// shared, parameterised by <paramref name="operationName"/> so each
/// adapter's log lines stay distinguishable.
/// </summary>
internal static class OperatorBackendRetryPipeline
{
    public static ResiliencePipeline<HttpResponseMessage> Build(
        ILogger logger,
        string operationName,
        int maxRetryAttempts,
        int perAttemptTimeoutSeconds,
        TimeSpan maxBackoff
    )
    {
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(
                new RetryStrategyOptions<HttpResponseMessage>
                {
                    MaxRetryAttempts = maxRetryAttempts,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    Delay = TimeSpan.FromMilliseconds(500),
                    MaxDelay = maxBackoff,
                    ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                        .Handle<HttpRequestException>()
                        .Handle<TimeoutRejectedException>()
                        .HandleResult(response => (int)response.StatusCode >= 500),
                    OnRetry = args =>
                    {
                        logger.LogWarning(
                            "{OperationName} attempt {Attempt} failed{StatusInfo}; retrying in {DelayMs}ms.",
                            operationName,
                            args.AttemptNumber + 1,
                            args.Outcome.Result is { } result
                                ? $" (HTTP {(int)result.StatusCode})"
                                : string.Empty,
                            (long)args.RetryDelay.TotalMilliseconds
                        );
                        return ValueTask.CompletedTask;
                    },
                }
            )
            .AddTimeout(
                new TimeoutStrategyOptions
                {
                    Timeout = TimeSpan.FromSeconds(perAttemptTimeoutSeconds),
                    OnTimeout = args =>
                    {
                        logger.LogWarning(
                            "{OperationName} attempt timed out after {TimeoutSeconds}s.",
                            operationName,
                            args.Timeout.TotalSeconds
                        );
                        return ValueTask.CompletedTask;
                    },
                }
            );

        return builder.Build();
    }
}
