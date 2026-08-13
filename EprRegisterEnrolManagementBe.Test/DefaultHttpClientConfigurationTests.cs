using EprRegisterEnrolManagementBe.Test.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace EprRegisterEnrolManagementBe.Test;

/// <summary>
/// Regression coverage for RA-311 query-push-proxy-fix: "DefaultClient" is
/// used for CDP-service-to-CDP-service traffic (the operator backend push,
/// <c>HttpOperatorBackendPushAdapter</c>) and must never route through the
/// CDP Squid proxy — internal <c>cdp-int.defra.cloud</c> hostnames aren't on
/// Squid's outbound allow-list, so a proxied request there tunnel-fails
/// with a 502.
///
/// Before the fix, "DefaultClient" relied on "no explicit primary handler
/// configured" to mean "unproxied", which stopped being true once
/// <c>Program.cs</c> started setting the process-wide
/// <see cref="System.Net.Http.HttpClient.DefaultProxy"/> for the GOV.UK
/// Notify SDK's benefit: a client factory-built client with no explicit
/// handler silently inherits <c>DefaultProxy</c>. This worked locally
/// (no <c>HTTPS_PROXY</c> set, so <c>DefaultProxy</c> stayed at the
/// framework default) but broke in CDP (where <c>HTTPS_PROXY</c> is always
/// injected).
///
/// Uses <see cref="IHttpMessageHandlerFactory"/> directly (rather than
/// <see cref="IHttpClientFactory.CreateClient"/>) so the test can unwrap the
/// actual configured primary handler and assert on
/// <see cref="HttpClientHandler.UseProxy"/> — the property that must be
/// explicitly <c>false</c> for this regression to be closed.
/// </summary>
public class DefaultHttpClientConfigurationTests
{
    private readonly MongoIntegrationFixture _fixture;

    public DefaultHttpClientConfigurationTests(MongoIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task DefaultClient_explicitly_disables_the_proxy()
    {
        await using var factory = new EphemeralMongoTestFactory(_fixture, "default-client-proxy");
        var handlerFactory = factory.Services.GetRequiredService<IHttpMessageHandlerFactory>();

        var handler = UnwrapPrimaryHandler(handlerFactory.CreateHandler("DefaultClient"));

        var httpClientHandler = Assert.IsType<HttpClientHandler>(handler);
        Assert.False(
            httpClientHandler.UseProxy,
            "\"DefaultClient\" must explicitly opt out of HttpClient.DefaultProxy " +
            "(process-wide, set in Program.cs for the Notify SDK) — otherwise it " +
            "silently routes CDP-service-to-CDP-service traffic through Squid in " +
            "any environment where HTTPS_PROXY is set, which is every CDP " +
            "environment. See RA-311 query-push-proxy-fix.");
    }

    /// <summary>
    /// <see cref="IHttpMessageHandlerFactory.CreateHandler"/> returns the
    /// full delegating-handler chain (header propagation, lifetime
    /// tracking, etc.) built on top of the configured primary handler.
    /// Walk to the innermost, non-delegating handler.
    /// </summary>
    private static HttpMessageHandler UnwrapPrimaryHandler(HttpMessageHandler handler)
    {
        while (handler is DelegatingHandler { InnerHandler: { } inner })
        {
            handler = inner;
        }
        return handler;
    }
}
