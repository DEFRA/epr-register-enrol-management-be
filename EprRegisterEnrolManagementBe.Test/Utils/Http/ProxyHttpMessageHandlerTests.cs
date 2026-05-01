using System.Net;
using EprRegisterEnrolManagementBe.Utils.Http;

namespace EprRegisterEnrolManagementBe.Test.Utils.Http;

/// <summary>
/// epr-9da: HTTP_PROXY env vars routinely embed user:pass@ credentials.
/// The handler's WebProxy.Address must never carry that user-info or it
/// will surface in any ToString() / serialization / accidental log of
/// the handler state. These tests pin the strip-and-relocate contract
/// of <see cref="ProxyHttpMessageHandler.ParseProxyUri"/>.
/// </summary>
public class ProxyHttpMessageHandlerTests
{
    [Fact]
    public void Credentialed_uri_strips_user_info_off_address()
    {
        var (address, credentials) = ProxyHttpMessageHandler.ParseProxyUri(
            "http://alice:s3cret@proxy.local:3128");

        Assert.Empty(address.UserInfo);
        Assert.DoesNotContain("alice", address.ToString());
        Assert.DoesNotContain("s3cret", address.ToString());
        Assert.Equal("proxy.local", address.Host);
        Assert.Equal(3128, address.Port);

        Assert.NotNull(credentials);
        Assert.Equal("alice", credentials!.UserName);
        Assert.Equal("s3cret", credentials.Password);
    }

    [Fact]
    public void Credentialed_uri_unescapes_percent_encoded_special_chars()
    {
        // Real-world: "p@ss/w:rd" needs encoding when stuffed into a URI.
        // The NetworkCredential consumer needs the decoded form.
        var (address, credentials) = ProxyHttpMessageHandler.ParseProxyUri(
            "http://alice%40corp:p%40ss%2Fw%3Ard@proxy.local:3128");

        Assert.Empty(address.UserInfo);
        Assert.NotNull(credentials);
        Assert.Equal("alice@corp", credentials!.UserName);
        Assert.Equal("p@ss/w:rd", credentials.Password);
    }

    [Fact]
    public void Uri_without_credentials_round_trips_without_a_NetworkCredential()
    {
        var (address, credentials) = ProxyHttpMessageHandler.ParseProxyUri(
            "http://proxy.local:3128");

        Assert.Empty(address.UserInfo);
        Assert.Equal("proxy.local", address.Host);
        Assert.Equal(3128, address.Port);
        Assert.Null(credentials);
    }

    [Fact]
    public void Constructor_with_credentialed_uri_sets_address_without_user_info_and_keeps_credentials_separate()
    {
        // Drives the production constructor path through the internal
        // test seam so we exercise WebProxy assignment, not just the
        // parser. WebProxy is not a sealed seam — the assertion is that
        // the resulting Proxy.Address has no UserInfo, regardless of
        // what was passed in.
        using var handler = new ProxyHttpMessageHandler(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProxyHttpMessageHandler>.Instance,
            "http://alice:s3cret@proxy.local:3128");

        var webProxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.NotNull(webProxy.Address);
        Assert.Empty(webProxy.Address!.UserInfo);
        Assert.DoesNotContain("s3cret", webProxy.Address.ToString());

        var credentials = Assert.IsType<NetworkCredential>(webProxy.Credentials);
        Assert.Equal("alice", credentials.UserName);
        Assert.Equal("s3cret", credentials.Password);
        Assert.True(handler.UseProxy);
    }

    [Fact]
    public void Constructor_with_no_proxy_uri_disables_proxy()
    {
        using var handler = new ProxyHttpMessageHandler(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProxyHttpMessageHandler>.Instance,
            proxyUri: null);

        Assert.False(handler.UseProxy);
    }
}
