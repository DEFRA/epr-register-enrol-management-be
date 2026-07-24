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
/// RA-311/MBE-1: the real outbound adapter. Posts to OBE-2's real contract
/// (<c>workItemId</c> in the route, <c>{ queryNote, sectionKeys }</c> in the
/// body), signs with the shared v2 HMAC canonical payload when a secret is
/// configured, retries transient (5xx / transport) failures only, and never
/// throws its way out of <see cref="IOperatorBackendPushAdapter.PushQueryRaisedAsync"/>.
///
/// All tests inject a zero-delay retry pipeline so retry-path tests run at
/// unit-test speed rather than exercising the production pipeline's real
/// (jittered exponential) backoff.
/// </summary>
public class HttpOperatorBackendPushAdapterTests
{
    private const string BaseUrl = "https://operator-backend.example.test";

    private static readonly string[] s_sectionKeys = ["business-plan", "prn-tonnage"];

    private static (HttpOperatorBackendPushAdapter Adapter, FakeHttpMessageHandler Handler) BuildSut(
        string? sharedSecret = null, string? url = BaseUrl)
    {
        var handler = new FakeHttpMessageHandler();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));

        var config = Options.Create(new OperatorBackendApiConfig
        {
            Url = url ?? string.Empty,
            ClientId = "epr-register-enrol-management-be",
            SharedSecret = sharedSecret,
        });

        var adapter = new HttpOperatorBackendPushAdapter(
            httpClientFactory, config, NullLogger<HttpOperatorBackendPushAdapter>.Instance, FastRetryPipeline());
        return (adapter, handler);
    }

    /// <summary>Same shape as the adapter's production pipeline (2 retries, 5xx/transport only), no delay.</summary>
    private static ResiliencePipeline<HttpResponseMessage> FastRetryPipeline() =>
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.Zero,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>()
                    .HandleResult(response => (int)response.StatusCode >= 500),
            })
            .Build();

    [Fact]
    public async Task PushQueryRaisedAsync_fails_fast_when_url_is_not_configured()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut(url: string.Empty);

        var result = await adapter.PushQueryRaisedAsync(Guid.NewGuid(), "why", s_sectionKeys, ct);

        Assert.False(result.IsSuccess);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task PushQueryRaisedAsync_posts_to_the_corrected_case_management_query_endpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.Respond(HttpStatusCode.OK);
        var workItemId = Guid.NewGuid();

        var result = await adapter.PushQueryRaisedAsync(workItemId, "why", s_sectionKeys, ct);

        Assert.True(result.IsSuccess);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        // Full composed absolute URI, not just a prefix — workItemId now
        // lives in the route (MBE-F1), matching OBE-2's real contract:
        // POST api/v1/accreditation-applications/case-management/{workItemId}/query
        Assert.Equal(
            $"{BaseUrl}/api/v1/accreditation-applications/case-management/{workItemId}/query",
            handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task PushQueryRaisedAsync_sends_the_client_id_header()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.Respond(HttpStatusCode.OK);

        await adapter.PushQueryRaisedAsync(Guid.NewGuid(), "why", s_sectionKeys, ct);

        Assert.Equal(
            "epr-register-enrol-management-be",
            handler.LastRequest!.Headers.GetValues("x-cdp-cognito-client-id").Single());
    }

    [Fact]
    public async Task PushQueryRaisedAsync_body_contains_queryNote_and_sectionKeys_but_not_workItemId()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.Respond(HttpStatusCode.OK);
        var workItemId = Guid.NewGuid();

        await adapter.PushQueryRaisedAsync(workItemId, "Tonnage does not reconcile", s_sectionKeys, ct);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;

        Assert.Equal("Tonnage does not reconcile", root.GetProperty("queryNote").GetString());
        var sectionKeys = root.GetProperty("sectionKeys").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(s_sectionKeys, sectionKeys);

        // MBE-F2: workItemId moved to the URL — it must NOT reappear in the
        // body under either casing. A raw substring check would still pass
        // if the GUID leaked into an unrelated field, so check the actual
        // property set instead.
        foreach (var property in root.EnumerateObject())
        {
            Assert.NotEqual("workitemid", property.Name, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task PushQueryRaisedAsync_omits_signature_headers_when_no_secret_is_configured()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut(sharedSecret: null);
        handler.Respond(HttpStatusCode.OK);

        await adapter.PushQueryRaisedAsync(Guid.NewGuid(), "why", s_sectionKeys, ct);

        Assert.False(handler.LastRequest!.Headers.Contains("x-cdp-auth-signature"));
        Assert.False(handler.LastRequest.Headers.Contains("x-cdp-auth-timestamp"));
        Assert.False(handler.LastRequest.Headers.Contains("x-cdp-auth-nonce"));
    }

    [Fact]
    public async Task PushQueryRaisedAsync_signs_the_request_when_a_secret_is_configured()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut(sharedSecret: "shh-its-a-secret");
        handler.Respond(HttpStatusCode.OK);

        await adapter.PushQueryRaisedAsync(Guid.NewGuid(), "why", s_sectionKeys, ct);

        Assert.True(handler.LastRequest!.Headers.Contains("x-cdp-auth-signature"));
        Assert.True(handler.LastRequest.Headers.Contains("x-cdp-auth-timestamp"));
        Assert.True(handler.LastRequest.Headers.Contains("x-cdp-auth-nonce"));
    }

    [Fact]
    public async Task PushQueryRaisedAsync_fails_on_a_non_success_status_code()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.Respond(HttpStatusCode.InternalServerError, "boom");

        var result = await adapter.PushQueryRaisedAsync(Guid.NewGuid(), "why", s_sectionKeys, ct);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task PushQueryRaisedAsync_never_throws_when_sending_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.ThrowOnSend = new HttpRequestException("connection refused");

        var result = await adapter.PushQueryRaisedAsync(Guid.NewGuid(), "why", s_sectionKeys, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal("connection refused", result.ErrorMessage);
    }

    [Fact]
    public async Task PushQueryRaisedAsync_retries_a_5xx_and_succeeds_once_the_backend_recovers()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.RespondSequence(
            (HttpStatusCode.InternalServerError, "boom"),
            (HttpStatusCode.OK, string.Empty));

        var result = await adapter.PushQueryRaisedAsync(Guid.NewGuid(), "why", s_sectionKeys, ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task PushQueryRaisedAsync_retries_a_transport_exception_and_succeeds_once_the_backend_recovers()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.ThrowOnSendForFirstNCalls = 1;
        handler.Respond(HttpStatusCode.OK);

        var result = await adapter.PushQueryRaisedAsync(Guid.NewGuid(), "why", s_sectionKeys, ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task PushQueryRaisedAsync_does_not_retry_a_4xx_response()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.Respond(HttpStatusCode.NotFound, "not found");

        var result = await adapter.PushQueryRaisedAsync(Guid.NewGuid(), "why", s_sectionKeys, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task PushQueryRaisedAsync_gives_up_after_exhausting_retries_on_a_persistent_5xx()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.Respond(HttpStatusCode.InternalServerError, "still down");

        var result = await adapter.PushQueryRaisedAsync(Guid.NewGuid(), "why", s_sectionKeys, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(3, handler.CallCount); // 1 initial attempt + 2 retries
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }
        public Exception? ThrowOnSend { get; set; }
        public int ThrowOnSendForFirstNCalls { get; set; }
        public int CallCount { get; private set; }

        private readonly Queue<(HttpStatusCode Status, string Content)> _responses = new();
        private (HttpStatusCode Status, string Content) _lastResponse = (HttpStatusCode.OK, string.Empty);

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
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            // Read the body here, before the caller's HttpClient disposes the
            // request content once SendAsync returns.
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
