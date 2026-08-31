using System.Net;
using System.Net.Http;
using System.Text.Json;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Utils.Logging;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Polly;
using Polly.Retry;

namespace EprRegisterEnrolManagementBe.Test.Integrations.OperatorBackend;

/// <summary>
/// RA-448 phase 2: the real outbound adapter that resolves accreditation
/// numbers from the backend, replacing the retired local
/// AccreditationIdGenerator. Posts to the backend's confirmed phase 1
/// contract (organisationId/applicationId in the route,
/// { Nation, OrgId, Year, Regenerate } in the body), signs with the shared
/// v3 HMAC canonical payload when a secret is configured, retries transient
/// (5xx / transport) failures only, and never throws its way out of
/// <see cref="IAccreditationNumberAdapter.GenerateOrUpdateAccreditationNumberAsync"/>.
///
/// All tests inject a zero-delay retry pipeline so retry-path tests run at
/// unit-test speed rather than exercising the production pipeline's real
/// (jittered exponential) backoff.
/// </summary>
public class HttpAccreditationNumberAdapterTests
{
    private const string BaseUrl = "https://operator-backend.example.test";

    private static (
        HttpAccreditationNumberAdapter Adapter,
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

        var adapter = new HttpAccreditationNumberAdapter(
            httpClientFactory,
            config,
            NullLogger<HttpAccreditationNumberAdapter>.Instance,
            Substitute.For<IStructuredLogger<HttpAccreditationNumberAdapter>>(),
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

    private static Task<AccreditationNumberResult> Call(
        HttpAccreditationNumberAdapter adapter,
        CancellationToken ct,
        string organisationId = "500027",
        string applicationId = "app-1",
        Nation nation = Nation.England,
        int orgId = 500027,
        int year = 2025,
        bool regenerate = true,
        Guid correlationId = default
    ) =>
        adapter.GenerateOrUpdateAccreditationNumberAsync(
            new AccreditationNumberRequest(
                organisationId,
                applicationId,
                nation,
                orgId,
                year,
                regenerate,
                correlationId
            ),
            ct
        );

    private static string SuccessBody(string accreditationReference) =>
        JsonSerializer.Serialize(new { accreditationReference });

    [Fact]
    public async Task Fails_fast_when_url_is_not_configured()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut(url: string.Empty);

        var result = await Call(adapter, ct);

        Assert.False(result.IsSuccess);
        Assert.Null(handler.LastRequest);
    }

    /// <summary>
    /// RA-448 phase 2 review follow-up: Enabled=false must stay a valid,
    /// behaviour-neutral state (MBE-F5) even when Url happens to be
    /// pre-populated ahead of a rollout — this adapter must not fire a real
    /// signed HTTP call in that state, unlike a check on Url alone would.
    /// </summary>
    [Fact]
    public async Task Fails_fast_and_does_not_call_out_when_disabled_even_with_a_configured_url()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut(enabled: false);

        var result = await Call(adapter, ct);

        Assert.False(result.IsSuccess);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task Sends_the_correlation_id_header()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.Respond(HttpStatusCode.OK, SuccessBody("A25ER5000270036WO"));
        var correlationId = Guid.NewGuid();

        await Call(adapter, ct, correlationId: correlationId);

        Assert.Equal(
            correlationId.ToString(),
            handler.LastRequest!.Headers.GetValues("X-Correlation-Id").Single()
        );
    }

    [Fact]
    public async Task Posts_to_the_confirmed_organisation_application_accreditation_number_route()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.Respond(HttpStatusCode.OK, SuccessBody("A25ER5000270036WO"));

        var result = await Call(adapter, ct, organisationId: "org-1", applicationId: "app-42");

        Assert.True(result.IsSuccess);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal(
            $"{BaseUrl}/api/v1/accreditation-applications/org-1/app-42/accreditation-number",
            handler.LastRequest.RequestUri!.ToString()
        );
    }

    [Fact]
    public async Task Returns_the_accreditationReference_from_the_response_body()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.Respond(HttpStatusCode.OK, SuccessBody("A25ER5000270036WO"));

        var result = await Call(adapter, ct);

        Assert.True(result.IsSuccess);
        Assert.Equal("A25ER5000270036WO", result.AccreditationNumber);
    }

    [Fact]
    public async Task Fails_when_the_response_body_carries_no_accreditationReference()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.Respond(HttpStatusCode.OK, "{}");

        var result = await Call(adapter, ct);

        Assert.False(result.IsSuccess);
        Assert.Null(result.AccreditationNumber);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task Body_contains_nation_orgId_year_and_regenerate()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.Respond(HttpStatusCode.OK, SuccessBody("A25ER5000270036WO"));

        await Call(
            adapter,
            ct,
            nation: Nation.Scotland,
            orgId: 500099,
            year: 2028,
            regenerate: true
        );

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;

        Assert.Equal("Scotland", root.GetProperty("nation").GetString());
        Assert.Equal(500099, root.GetProperty("orgId").GetInt32());
        Assert.Equal(2028, root.GetProperty("year").GetInt32());
        Assert.True(root.GetProperty("regenerate").GetBoolean());
    }

    [Fact]
    public async Task Sends_the_client_id_header()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.Respond(HttpStatusCode.OK, SuccessBody("A25ER5000270036WO"));

        await Call(adapter, ct);

        Assert.Equal(
            "epr-register-enrol-management-be",
            handler.LastRequest!.Headers.GetValues("x-cdp-client-id").Single()
        );
    }

    [Fact]
    public async Task Omits_signature_headers_when_no_secret_is_configured()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut(sharedSecret: null);
        handler.Respond(HttpStatusCode.OK, SuccessBody("A25ER5000270036WO"));

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
        handler.Respond(HttpStatusCode.OK, SuccessBody("A25ER5000270036WO"));

        await Call(adapter, ct);

        Assert.True(handler.LastRequest!.Headers.Contains("x-cdp-auth-signature"));
        Assert.True(handler.LastRequest.Headers.Contains("x-cdp-auth-timestamp"));
        Assert.True(handler.LastRequest.Headers.Contains("x-cdp-auth-nonce"));
    }

    [Fact]
    public async Task Fails_on_a_non_success_status_code()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.Respond(HttpStatusCode.InternalServerError, "boom");

        var result = await Call(adapter, ct);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
    }

    /// <summary>
    /// RA-448 phase 2 review follow-up: genuine caller-token cancellation
    /// (request aborted, upstream timeout) must propagate as a cancellation,
    /// not be converted into an AccreditationNumberResult.Failure the caller
    /// would otherwise treat as a definite, retriable business outcome.
    /// </summary>
    [Fact]
    public async Task Propagates_cancellation_rather_than_returning_a_failure_result()
    {
        var (adapter, _) = BuildSut();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(adapter, cts.Token));
    }

    [Fact]
    public async Task Never_throws_when_sending_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.ThrowOnSend = new HttpRequestException("connection refused");

        var result = await Call(adapter, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal("connection refused", result.ErrorMessage);
    }

    [Fact]
    public async Task Retries_a_5xx_and_succeeds_once_the_backend_recovers()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.RespondSequence(
            (HttpStatusCode.InternalServerError, "boom"),
            (HttpStatusCode.OK, SuccessBody("A25ER5000270036WO"))
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
        handler.Respond(HttpStatusCode.OK, SuccessBody("A25ER5000270036WO"));

        var result = await Call(adapter, ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Does_not_retry_a_4xx_response()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.Respond(HttpStatusCode.Conflict, "no registration number yet");

        var result = await Call(adapter, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Gives_up_after_exhausting_retries_on_a_persistent_5xx()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.Respond(HttpStatusCode.InternalServerError, "still down");

        var result = await Call(adapter, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(3, handler.CallCount); // 1 initial attempt + 2 retries
    }

    // ─────── real (non-test-substituted) BuildRetryPipeline branch coverage ───────
    //
    // Unlike HttpOperatorBackendPushAdapter's pipeline, this adapter's
    // per-attempt timeout/retry budget is fixed internal constants, not
    // sourced from OperatorBackendApiConfig.RequestTimeoutSeconds (see the
    // class doc comment for why) — so there is no "timeout disabled" branch
    // to exercise here; these tests just cover the real (non-injected)
    // pipeline's happy path and retry behaviour.

    [Fact]
    public async Task Succeeds_via_the_real_pipeline()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new FakeHttpMessageHandler();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var config = Options.Create(
            new OperatorBackendApiConfig
            {
                Enabled = true,
                Url = BaseUrl,
                ClientId = "epr-register-enrol-management-be",
            }
        );
        // No retryPipeline argument: forces the constructor to build the
        // real, private BuildRetryPipeline.
        var adapter = new HttpAccreditationNumberAdapter(
            httpClientFactory,
            config,
            NullLogger<HttpAccreditationNumberAdapter>.Instance,
            Substitute.For<IStructuredLogger<HttpAccreditationNumberAdapter>>()
        );
        handler.Respond(HttpStatusCode.OK, SuccessBody("A25ER5000270036WO"));

        var result = await Call(adapter, ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Real_pipeline_retries_a_5xx_result_and_recovers()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new FakeHttpMessageHandler();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var config = Options.Create(
            new OperatorBackendApiConfig
            {
                Enabled = true,
                Url = BaseUrl,
                ClientId = "epr-register-enrol-management-be",
            }
        );
        var adapter = new HttpAccreditationNumberAdapter(
            httpClientFactory,
            config,
            NullLogger<HttpAccreditationNumberAdapter>.Instance,
            Substitute.For<IStructuredLogger<HttpAccreditationNumberAdapter>>()
        );
        handler.RespondSequence(
            (HttpStatusCode.InternalServerError, "boom"),
            (HttpStatusCode.OK, SuccessBody("A25ER5000270036WO"))
        );

        var result = await Call(adapter, ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.CallCount);
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
