using System.Net;
using System.Net.Http;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.Integrations.OperatorBackend;

/// <summary>
/// RA-311/MBE-1: the real outbound adapter. Signs with the shared v2 HMAC
/// canonical payload when a secret is configured, never throws its way out
/// of <see cref="IOperatorBackendPushAdapter.PushQueryRaisedAsync"/>.
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
            httpClientFactory, config, NullLogger<HttpOperatorBackendPushAdapter>.Instance);
        return (adapter, handler);
    }

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
    public async Task PushQueryRaisedAsync_posts_to_the_configured_base_url()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.Respond(HttpStatusCode.OK);

        var result = await adapter.PushQueryRaisedAsync(Guid.NewGuid(), "why", s_sectionKeys, ct);

        Assert.True(result.IsSuccess);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.StartsWith(BaseUrl, handler.LastRequest.RequestUri!.ToString());
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
    public async Task PushQueryRaisedAsync_sends_workItemId_queryNote_and_sectionKeys_in_the_body()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, handler) = BuildSut();
        handler.Respond(HttpStatusCode.OK);
        var workItemId = Guid.NewGuid();

        await adapter.PushQueryRaisedAsync(workItemId, "Tonnage does not reconcile", s_sectionKeys, ct);

        var body = handler.LastRequestBody!;
        Assert.Contains(workItemId.ToString(), body);
        Assert.Contains("Tonnage does not reconcile", body);
        Assert.Contains("business-plan", body);
        Assert.Contains("prn-tonnage", body);
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

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }
        public Exception? ThrowOnSend { get; set; }
        private HttpStatusCode _statusCode = HttpStatusCode.OK;
        private string _content = string.Empty;

        public void Respond(HttpStatusCode statusCode, string content = "")
        {
            _statusCode = statusCode;
            _content = content;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
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

            return new HttpResponseMessage(_statusCode) { Content = new StringContent(_content) };
        }
    }
}
