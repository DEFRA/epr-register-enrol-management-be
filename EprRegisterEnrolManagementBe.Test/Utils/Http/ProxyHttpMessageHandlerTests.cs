using System.Net;
using EprRegisterEnrolManagementBe.Utils.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

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

    // ---------------------- epr-9g6: HTTPS_PROXY / NO_PROXY ----------------------

    [Fact]
    public void ResolveProxyEnvironmentValue_prefers_HTTPS_PROXY_over_HTTP_PROXY()
    {
        // Drive the resolver via process env vars set inside the test
        // and torn down on exit. Outbound traffic is HTTPS, so when
        // both are set HTTPS_PROXY must win.
        using var _h = new ScopedEnvironmentVariable("HTTPS_PROXY", "http://https-proxy.local:8443");
        using var _h2 = new ScopedEnvironmentVariable("https_proxy", null);
        using var _l = new ScopedEnvironmentVariable("HTTP_PROXY", "http://http-proxy.local:3128");
        using var _l2 = new ScopedEnvironmentVariable("http_proxy", null);

        Assert.Equal("http://https-proxy.local:8443", ProxyHttpMessageHandler.ResolveProxyEnvironmentValue());
    }

    [Fact]
    public void ResolveProxyEnvironmentValue_falls_back_to_HTTP_PROXY_when_HTTPS_PROXY_absent()
    {
        using var _h = new ScopedEnvironmentVariable("HTTPS_PROXY", null);
        using var _h2 = new ScopedEnvironmentVariable("https_proxy", null);
        using var _l = new ScopedEnvironmentVariable("HTTP_PROXY", "http://http-proxy.local:3128");
        using var _l2 = new ScopedEnvironmentVariable("http_proxy", null);

        Assert.Equal("http://http-proxy.local:3128", ProxyHttpMessageHandler.ResolveProxyEnvironmentValue());
    }

    [Fact]
    public void ResolveProxyEnvironmentValue_honours_lower_case_variants()
    {
        // Several CDP base images / shells set the lower-case form. The
        // resolver must accept either case.
        using var _h = new ScopedEnvironmentVariable("HTTPS_PROXY", null);
        using var _h2 = new ScopedEnvironmentVariable("https_proxy", "http://lower-https.local:8443");
        using var _l = new ScopedEnvironmentVariable("HTTP_PROXY", null);
        using var _l2 = new ScopedEnvironmentVariable("http_proxy", null);

        Assert.Equal("http://lower-https.local:8443", ProxyHttpMessageHandler.ResolveProxyEnvironmentValue());
    }

    [Fact]
    public void ResolveProxyEnvironmentValue_returns_null_when_nothing_set()
    {
        using var _h = new ScopedEnvironmentVariable("HTTPS_PROXY", null);
        using var _h2 = new ScopedEnvironmentVariable("https_proxy", null);
        using var _l = new ScopedEnvironmentVariable("HTTP_PROXY", null);
        using var _l2 = new ScopedEnvironmentVariable("http_proxy", null);

        Assert.Null(ProxyHttpMessageHandler.ResolveProxyEnvironmentValue());
    }

    [Fact]
    public void ResolveProxyEnvironmentValue_treats_empty_value_as_unset()
    {
        // A bare `export HTTPS_PROXY=` shouldn't override a real
        // HTTP_PROXY value or wedge UseProxy=true with an empty URI.
        using var _h = new ScopedEnvironmentVariable("HTTPS_PROXY", "");
        using var _h2 = new ScopedEnvironmentVariable("https_proxy", null);
        using var _l = new ScopedEnvironmentVariable("HTTP_PROXY", "http://http-proxy.local:3128");
        using var _l2 = new ScopedEnvironmentVariable("http_proxy", null);

        Assert.Equal("http://http-proxy.local:3128", ProxyHttpMessageHandler.ResolveProxyEnvironmentValue());
    }

    [Fact]
    public void NoProxy_entries_are_translated_into_a_bypass_list()
    {
        using var handler = new ProxyHttpMessageHandler(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProxyHttpMessageHandler>.Instance,
            "http://proxy.local:3128",
            noProxy: "internal.example, .corp.example, localhost");

        var webProxy = Assert.IsType<WebProxy>(handler.Proxy);
        // BypassList is built; concrete pattern strings are an
        // implementation detail, but the bypass behaviour is observable
        // via WebProxy.IsBypassed.
        Assert.True(webProxy.IsBypassed(new Uri("https://api.internal.example/v1")));
        Assert.True(webProxy.IsBypassed(new Uri("https://corp.example/")));
        Assert.True(webProxy.IsBypassed(new Uri("https://anything.corp.example/")));
        Assert.False(webProxy.IsBypassed(new Uri("https://api.elsewhere.example/")));
    }

    [Fact]
    public void NoProxy_wildcard_bypasses_everything()
    {
        using var handler = new ProxyHttpMessageHandler(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProxyHttpMessageHandler>.Instance,
            "http://proxy.local:3128",
            noProxy: "*");

        var webProxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.True(webProxy.IsBypassed(new Uri("https://api.elsewhere.example/")));
        Assert.True(webProxy.IsBypassed(new Uri("https://internal.corp/")));
    }

    [Fact]
    public void NoProxy_unset_leaves_default_bypass_behaviour_intact()
    {
        using var handler = new ProxyHttpMessageHandler(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProxyHttpMessageHandler>.Instance,
            "http://proxy.local:3128",
            noProxy: null);

        var webProxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.False(webProxy.IsBypassed(new Uri("https://api.elsewhere.example/")));
    }

    // ---------------------- epr-cuq: fail-closed in non-Development ----------------------
    //
    // docs/cdp-deployment.md requires HTTP(S)_PROXY for any external HTTP
    // call outside Development. The historical behaviour was warn-and-
    // continue with UseProxy=false; that meant a misconfigured deployment
    // would silently bypass the Squid proxy and egress outside the
    // controlled network the first time a real downstream HTTP client
    // was registered. Production / Test / Staging must now refuse to
    // construct the handler without a proxy URI; Development keeps the
    // warn-only path so local runs work without proxy infrastructure.

    [Fact]
    public void Production_constructor_throws_when_no_proxy_env_vars_are_set()
    {
        using var _h = new ScopedEnvironmentVariable("HTTPS_PROXY", null);
        using var _h2 = new ScopedEnvironmentVariable("https_proxy", null);
        using var _l = new ScopedEnvironmentVariable("HTTP_PROXY", null);
        using var _l2 = new ScopedEnvironmentVariable("http_proxy", null);
        var env = HostEnvironment("Production");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ProxyHttpMessageHandler(NullLogger<ProxyHttpMessageHandler>.Instance, env));

        // Message must name the missing env vars and the offending
        // environment so the failure is actionable in CDP logs.
        Assert.Contains("HTTPS_PROXY", ex.Message);
        Assert.Contains("HTTP_PROXY", ex.Message);
        Assert.Contains("Production", ex.Message);
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Test")]
    [InlineData("Performance")]
    public void Non_development_environments_all_fail_closed(string envName)
    {
        // Only Development is whitelisted. Any other environment name —
        // including custom CDP environments — must fail closed so a
        // typo (e.g. "Develop", "develpoment") doesn't accidentally
        // unlock direct egress.
        using var _h = new ScopedEnvironmentVariable("HTTPS_PROXY", null);
        using var _h2 = new ScopedEnvironmentVariable("https_proxy", null);
        using var _l = new ScopedEnvironmentVariable("HTTP_PROXY", null);
        using var _l2 = new ScopedEnvironmentVariable("http_proxy", null);
        var env = HostEnvironment(envName);

        Assert.Throws<InvalidOperationException>(() =>
            new ProxyHttpMessageHandler(NullLogger<ProxyHttpMessageHandler>.Instance, env));
    }

    [Fact]
    public void Development_constructor_warns_and_disables_proxy_when_env_vars_unset()
    {
        // Local-dev contract: no proxy infrastructure required.
        using var _h = new ScopedEnvironmentVariable("HTTPS_PROXY", null);
        using var _h2 = new ScopedEnvironmentVariable("https_proxy", null);
        using var _l = new ScopedEnvironmentVariable("HTTP_PROXY", null);
        using var _l2 = new ScopedEnvironmentVariable("http_proxy", null);
        var env = HostEnvironment("Development");

        using var handler = new ProxyHttpMessageHandler(
            NullLogger<ProxyHttpMessageHandler>.Instance, env);

        Assert.False(handler.UseProxy);
    }

    [Fact]
    public void Production_constructor_succeeds_when_HTTPS_PROXY_is_set()
    {
        using var _h = new ScopedEnvironmentVariable("HTTPS_PROXY", "http://proxy.local:3128");
        using var _h2 = new ScopedEnvironmentVariable("https_proxy", null);
        using var _l = new ScopedEnvironmentVariable("HTTP_PROXY", null);
        using var _l2 = new ScopedEnvironmentVariable("http_proxy", null);
        var env = HostEnvironment("Production");

        using var handler = new ProxyHttpMessageHandler(
            NullLogger<ProxyHttpMessageHandler>.Instance, env);

        Assert.True(handler.UseProxy);
        var webProxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Equal("proxy.local", webProxy.Address!.Host);
    }

    private static IHostEnvironment HostEnvironment(string envName)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(envName);
        return env;
    }

    // ---------------------- epr-0j2: malformed proxy URI redaction ----------------------

    [Fact]
    public void ParseProxyUri_throws_redacted_when_uri_is_malformed_with_credentials()
    {
        // Driving the parser with a value that has user:pass@ but is
        // syntactically malformed (illegal port). The baseline
        // behaviour propagated UriBuilder's UriFormatException whose
        // Message embedded the original input verbatim — putting the
        // password into Serilog's startup-failure log. The contract
        // under test: neither the username nor the password appears
        // anywhere in the resulting exception chain.
        const string user = "alice";
        const string secret = "s3cret";
        var malformed = $"http://{user}:{secret}@proxy.local:port-not-a-number";

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProxyHttpMessageHandler.ParseProxyUri(malformed));

        AssertNoCredentialLeak(ex, user, secret);
    }

    [Fact]
    public void RedactProxyUri_replaces_user_info_with_asterisks()
    {
        Assert.Equal(
            "http://***:***@proxy.local:3128/path",
            ProxyHttpMessageHandler.RedactProxyUri("http://alice:s3cret@proxy.local:3128/path"));
    }

    [Fact]
    public void RedactProxyUri_leaves_uri_without_credentials_unchanged()
    {
        Assert.Equal(
            "http://proxy.local:3128/",
            ProxyHttpMessageHandler.RedactProxyUri("http://proxy.local:3128/"));
    }

    [Fact]
    public void RedactProxyUri_does_not_treat_at_in_path_as_user_info()
    {
        // The '@' here is in the path, not in the authority. The
        // redactor must not over-redact and turn this into "***:***@".
        Assert.Equal(
            "http://proxy.local:3128/dir@thing",
            ProxyHttpMessageHandler.RedactProxyUri("http://proxy.local:3128/dir@thing"));
    }

    private static void AssertNoCredentialLeak(Exception ex, string user, string secret)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            Assert.DoesNotContain(secret, current.Message);
            Assert.DoesNotContain(user, current.Message);
            if (current.StackTrace is { } stack)
            {
                Assert.DoesNotContain(secret, stack);
            }
        }
    }
}

/// <summary>
/// Test helper: scope an environment-variable change to a `using` block
/// so concurrent / subsequent tests don't observe leaked state.
/// </summary>
internal sealed class ScopedEnvironmentVariable : IDisposable
{
    private readonly string _name;
    private readonly string? _previous;

    public ScopedEnvironmentVariable(string name, string? value)
    {
        _name = name;
        _previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
}
