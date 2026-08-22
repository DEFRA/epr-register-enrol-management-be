using System.Net;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using Microsoft.Extensions.Logging.Abstractions;
using Polly.Timeout;

namespace EprRegisterEnrolManagementBe.Test.Integrations.OperatorBackend;

/// <summary>
/// Direct tests of the shared pipeline both operator-backend adapters build
/// on (extracted from HttpAccreditationNumberAdapter and
/// HttpOverseasSiteRecyclingOperationsAdapter once their own copies were
/// flagged as duplicated code). Both adapters' own test suites inject a
/// simplified retry-only pipeline for speed (see e.g.
/// HttpOverseasSiteRecyclingOperationsAdapterTests.FastRetryPipeline), so
/// this is the only place the real per-attempt timeout behaviour this
/// class adds is exercised.
/// </summary>
public class OperatorBackendRetryPipelineTests
{
    [Fact]
    public async Task Retries_a_per_attempt_timeout_and_succeeds_once_the_call_completes_in_time()
    {
        var pipeline = OperatorBackendRetryPipeline.Build(
            NullLogger.Instance,
            "Test operation",
            maxRetryAttempts: 2,
            perAttemptTimeoutSeconds: 1,
            maxBackoff: TimeSpan.Zero
        );
        var attempt = 0;

        var response = await pipeline.ExecuteAsync(
            async ct =>
            {
                attempt++;
                // First attempt sleeps past the 1s per-attempt timeout, so
                // Polly's AddTimeout cancels it (TimeoutRejectedException,
                // handled by ShouldHandle -> retried); the second attempt
                // returns immediately.
                if (attempt == 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, attempt);
    }

    [Fact]
    public async Task Exhausts_retries_and_throws_TimeoutRejectedException_when_every_attempt_times_out()
    {
        var pipeline = OperatorBackendRetryPipeline.Build(
            NullLogger.Instance,
            "Test operation",
            maxRetryAttempts: 1,
            perAttemptTimeoutSeconds: 1,
            maxBackoff: TimeSpan.Zero
        );

        await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
            pipeline
                .ExecuteAsync(
                    async ct =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), ct);
                        return new HttpResponseMessage(HttpStatusCode.OK);
                    },
                    TestContext.Current.CancellationToken
                )
                .AsTask()
        );
    }
}
