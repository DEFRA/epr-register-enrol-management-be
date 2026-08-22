using System.Net;
using System.Net.Http;
using System.Text.Json;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Polly;
using Polly.Retry;

namespace EprRegisterEnrolManagementBe.Test.Integrations.OperatorBackend;

/// <summary>
/// RA-469 AC16: the real outbound adapter that updates an overseas site's
/// recycling operation codes on behalf of a regulator during case review.
/// Structural sibling of <see cref="HttpAccreditationNumberAdapter"/> (same
/// IHttpClientFactory "DefaultClient"/IOptions&lt;OperatorBackendApiConfig&gt;/
/// Polly-retry/never-throws shape) but — unlike that adapter, which
/// deliberately signs with a null identity — signs with the REAL caller's
/// user id/name so the backend's audit record attributes the edit to the
/// acting regulator.
///
/// All tests inject a zero-delay retry pipeline so retry-path tests run at
/// unit-test speed rather than exercising the production pipeline's real
/// (jittered exponential) backoff.
/// </summary>
public class HttpOverseasSiteRecyclingOperationsAdapterTests
{
    private const string BaseUrl = "https://operator-backend.example.test";

    private static (
        HttpOverseasSiteRecyclingOperationsAdapter Adapter,
        FakeHttpMessageHandler Handler
    ) BuildSut(string? sharedSecret = null, string? url = BaseUrl, bool enabled = true)
    {
        var handler = new FakeHttpMessageHandler();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));

        var config = Options.Create(
            new OperatorBackendApiConfig
            {
                Enabled = enabled,
                Url = url ?? string.Empty,
                ClientId = "epr-register-enrol-management-be",
                SharedSecret = sharedSecret,
            }
        );

        var adapter = new HttpOverseasSiteRecyclingOperationsAdapter(
            httpClientFactory,
            config,
            NullLogger<HttpOverseasSiteRecyclingOperationsAdapter>.Instance,
            FastRetryPipeline()
        );
        return (adapter, handler);
    }

    private static ResiliencePipeline<HttpResponseMessage> FastRetryPipeline() =>
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(
                new RetryStrategyOptions<HttpResponseMessage>
                {
                    MaxRetryAttempts = 2,
                    Delay = TimeSpan.Zero,
                    ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                        .Handle<HttpRequestException>()
                        .Handle<TaskCanceledException>()
                        .HandleResult(response => (int)response.StatusCode >= 500),
                }
            )
            .Build();

    private static Task<OverseasSiteRecyclingOperationsResult> Call(
        HttpOverseasSiteRecyclingOperationsAdapter adapter,
        CancellationToken ct,
        string organisationId = "500027",
        string applicationId = "app-1",
        string siteId = "site-1",
        IReadOnlyList<string>? operationCodes = null,
        string? userId = "alice-1",
        string? userName = "Alice Example",
        Guid correlationId = default
    ) =>
        adapter.UpdateRecyclingOperationsAsync(
            new OverseasSiteRecyclingOperationsRequest(
                organisationId,
                applicationId,
                siteId,
                operationCodes ?? ["R3", "R4"],
                userId,
                userName,
                correlationId
            ),
            ct
        );

    private static string SuccessBody(string siteId) =>
        JsonSerializer.Serialize(new { id = siteId, operationCodes = new[] { "R3", "R4" } });

    private static string ValidationFailedBody(string errorCode, string field, string detail) =>
        JsonSerializer.Serialize(
            new
            {
                title = "Could not update recycling operations",
                detail,
                status = 400,
                errorCode,
                field,
            }
        );

    [Fact]
    public async Task Fails_fast_when_url_is_not_configured()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut(url: string.Empty);

        var result = await Call(adapter, ct);

        Assert.Equal(OverseasSiteRecyclingOperationsOutcome.TransientFailure, result.Outcome);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task Fails_fast_and_does_not_call_out_when_disabled_even_with_a_configured_url()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut(enabled: false);

        var result = await Call(adapter, ct);

        Assert.Equal(OverseasSiteRecyclingOperationsOutcome.TransientFailure, result.Outcome);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task Sends_the_correlation_id_header()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.Respond(HttpStatusCode.OK, SuccessBody("site-1"));
        var correlationId = Guid.NewGuid();

        await Call(adapter, ct, correlationId: correlationId);

        Assert.Equal(
            correlationId.ToString(),
            handler.LastRequest!.Headers.GetValues("X-Correlation-Id").Single()
        );
    }

    [Fact]
    public async Task Patches_the_confirmed_recycling_operations_route()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.Respond(HttpStatusCode.OK, SuccessBody("site-42"));

        var result = await Call(
            adapter,
            ct,
            organisationId: "org-1",
            applicationId: "app-42",
            siteId: "site-42"
        );

        Assert.True(result.IsSuccess);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Patch, handler.LastRequest!.Method);
        Assert.Equal(
            $"{BaseUrl}/api/v1/accreditation-applications/org-1/app-42/overseas-sites/site-42/recycling-operations",
            handler.LastRequest.RequestUri!.ToString()
        );
    }

    [Fact]
    public async Task Body_contains_the_operation_codes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.Respond(HttpStatusCode.OK, SuccessBody("site-1"));

        await Call(adapter, ct, operationCodes: ["R12", "R13", "R3"]);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var codes = body
            .RootElement.GetProperty("operationCodes")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();

        Assert.Equal(["R12", "R13", "R3"], codes);
    }

    [Fact]
    public async Task Returns_the_site_json_from_the_response_body_on_success()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        var body = SuccessBody("site-1");
        handler.Respond(HttpStatusCode.OK, body);

        var result = await Call(adapter, ct);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.SiteJson);
        using var parsed = JsonDocument.Parse(result.SiteJson!);
        Assert.Equal("site-1", parsed.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Sends_the_client_id_header()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.Respond(HttpStatusCode.OK, SuccessBody("site-1"));

        await Call(adapter, ct);

        Assert.Equal(
            "epr-register-enrol-management-be",
            handler.LastRequest!.Headers.GetValues("x-cdp-client-id").Single()
        );
    }

    /// <summary>
    /// RA-469: the whole reason this adapter exists rather than reusing
    /// HttpAccreditationNumberAdapter's null/null pattern — the backend
    /// audit record must attribute the edit to the real acting regulator.
    /// </summary>
    [Fact]
    public async Task Signs_with_the_real_callers_user_id_and_name_not_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.Respond(HttpStatusCode.OK, SuccessBody("site-1"));

        await Call(adapter, ct, userId: "alice-1", userName: "Alice Example");

        Assert.Equal("alice-1", handler.LastRequest!.Headers.GetValues("x-cdp-user-id").Single());
        Assert.Equal(
            "Alice Example",
            handler.LastRequest.Headers.GetValues("x-cdp-user-name").Single()
        );
    }

    [Fact]
    public async Task Omits_signature_headers_when_no_secret_is_configured()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut(sharedSecret: null);
        handler.Respond(HttpStatusCode.OK, SuccessBody("site-1"));

        await Call(adapter, ct);

        Assert.False(handler.LastRequest!.Headers.Contains("x-cdp-auth-signature"));
        Assert.False(handler.LastRequest.Headers.Contains("x-cdp-auth-timestamp"));
        Assert.False(handler.LastRequest.Headers.Contains("x-cdp-auth-nonce"));
    }

    [Fact]
    public async Task Signs_the_request_when_a_secret_is_configured()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut(sharedSecret: "shh-its-a-secret");
        handler.Respond(HttpStatusCode.OK, SuccessBody("site-1"));

        await Call(adapter, ct);

        Assert.True(handler.LastRequest!.Headers.Contains("x-cdp-auth-signature"));
        Assert.True(handler.LastRequest.Headers.Contains("x-cdp-auth-timestamp"));
        Assert.True(handler.LastRequest.Headers.Contains("x-cdp-auth-nonce"));
    }

    [Fact]
    public async Task Backend_400_returns_ValidationFailed_with_errorCode_and_field_from_the_body()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.Respond(
            HttpStatusCode.BadRequest,
            ValidationFailedBody(
                "accompanying-code-required",
                "operationCodes",
                "R12/R13 require at least one of R3/R4/R5."
            )
        );

        var result = await Call(adapter, ct);

        Assert.Equal(OverseasSiteRecyclingOperationsOutcome.ValidationFailed, result.Outcome);
        Assert.Equal("accompanying-code-required", result.ErrorCode);
        Assert.Equal("operationCodes", result.Field);
        Assert.Equal("R12/R13 require at least one of R3/R4/R5.", result.Message);
    }

    [Fact]
    public async Task Backend_400_with_unparseable_body_falls_back_to_a_generic_validation_failed_code()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.Respond(HttpStatusCode.BadRequest, "not json");

        var result = await Call(adapter, ct);

        Assert.Equal(OverseasSiteRecyclingOperationsOutcome.ValidationFailed, result.Outcome);
        Assert.Equal("validation-failed", result.ErrorCode);
    }

    [Fact]
    public async Task Backend_404_returns_NotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.Respond(HttpStatusCode.NotFound, "");

        var result = await Call(adapter, ct);

        Assert.Equal(OverseasSiteRecyclingOperationsOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task Backend_409_returns_Conflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.Respond(HttpStatusCode.Conflict, "already updated");

        var result = await Call(adapter, ct);

        Assert.Equal(OverseasSiteRecyclingOperationsOutcome.Conflict, result.Outcome);
    }

    [Fact]
    public async Task Backend_5xx_returns_TransientFailure_after_the_retry_budget()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.Respond(HttpStatusCode.InternalServerError, "still down");

        var result = await Call(adapter, ct);

        Assert.Equal(OverseasSiteRecyclingOperationsOutcome.TransientFailure, result.Outcome);
        Assert.Equal(3, handler.CallCount); // 1 initial attempt + 2 retries
    }

    [Fact]
    public async Task Never_throws_when_sending_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.ThrowOnSend = new HttpRequestException("connection refused");

        var result = await Call(adapter, ct);

        Assert.Equal(OverseasSiteRecyclingOperationsOutcome.TransientFailure, result.Outcome);
        Assert.Equal("connection refused", result.Message);
    }

    [Fact]
    public async Task Retries_a_5xx_and_succeeds_once_the_backend_recovers()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.RespondSequence(
            (HttpStatusCode.InternalServerError, "boom"),
            (HttpStatusCode.OK, SuccessBody("site-1"))
        );

        var result = await Call(adapter, ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Retries_a_transport_exception_and_succeeds_once_the_backend_recovers()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.ThrowOnSendForFirstNCalls = 1;
        handler.Respond(HttpStatusCode.OK, SuccessBody("site-1"));

        var result = await Call(adapter, ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Does_not_retry_a_4xx_response()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.Respond(HttpStatusCode.Conflict, "already updated");

        var result = await Call(adapter, ct);

        Assert.Equal(OverseasSiteRecyclingOperationsOutcome.Conflict, result.Outcome);
        Assert.Equal(1, handler.CallCount);
    }

    /// <summary>
    /// RA-448-phase-2-style follow-up: genuine caller-token cancellation
    /// (request aborted, upstream timeout) must propagate as a
    /// cancellation, not be converted into a TransientFailure result the
    /// caller would otherwise treat as a definite, retriable business
    /// outcome.
    /// </summary>
    [Fact]
    public async Task Propagates_cancellation_rather_than_returning_a_failure_result()
    {
        var (adapter, _) = BuildSut();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(adapter, cts.Token));
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }
        public Exception? ThrowOnSend { get; set; }
        public int ThrowOnSendForFirstNCalls { get; set; }
        public int CallCount { get; private set; }

        private readonly Queue<(HttpStatusCode Status, string Content)> _responses = new();
        private (HttpStatusCode Status, string Content) _lastResponse = (
            HttpStatusCode.OK,
            string.Empty
        );

        public void Respond(HttpStatusCode statusCode, string content = "")
        {
            _lastResponse = (statusCode, content);
            _responses.Clear();
        }

        /// <summary>Dequeues one response per call; once exhausted, keeps repeating the last one.</summary>
        public void RespondSequence(params (HttpStatusCode Status, string Content)[] responses)
        {
            _responses.Clear();
            foreach (var response in responses)
            {
                _responses.Enqueue(response);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            CallCount++;
            LastRequest = request;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            if (ThrowOnSend is not null)
            {
                throw ThrowOnSend;
            }
            if (CallCount <= ThrowOnSendForFirstNCalls)
            {
                throw new HttpRequestException("connection refused");
            }

            var (status, content) = _responses.Count > 0 ? _responses.Dequeue() : _lastResponse;
            if (_responses.Count == 0)
            {
                _lastResponse = (status, content);
            }

            return new HttpResponseMessage(status) { Content = new StringContent(content) };
        }
    }
}
