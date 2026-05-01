using System.Net;

namespace EprRegisterEnrolManagementBe.Utils.Http;

public class ProxyHttpMessageHandler : HttpClientHandler
{
    public ProxyHttpMessageHandler(ILogger<ProxyHttpMessageHandler> logger)
        : this(logger, Environment.GetEnvironmentVariable("HTTP_PROXY"))
    {
    }

    /// <summary>
    /// Internal seam for tests (epr-9da) — keeps the production
    /// constructor's HTTP_PROXY env-var read intact while letting the
    /// strip-credentials behaviour be exercised without mutating
    /// process state.
    /// </summary>
    internal ProxyHttpMessageHandler(ILogger<ProxyHttpMessageHandler> logger, string? proxyUri)
    {
        var proxy = new WebProxy { BypassProxyOnLocal = true };
        if (proxyUri != null)
        {
            logger.LogDebug("Creating proxy http client");
            var (sanitisedAddress, credentials) = ParseProxyUri(proxyUri);
            proxy.Address = sanitisedAddress;
            // Credentials live on a NetworkCredential so they cannot
            // leak via WebProxy.Address.ToString() / serialization /
            // accidental logging. Address itself never carries user-info.
            if (credentials is not null)
            {
                proxy.Credentials = credentials;
            }
        }
        else
        {
            logger.LogWarning("HTTP_PROXY is NOT set, proxy client will be disabled");
        }

        Proxy = proxy;
        UseProxy = proxyUri != null;
    }

    /// <summary>
    /// Parse a proxy URI and split any embedded <c>user:pass@</c> credentials
    /// off the address. Returns the credential-free address (always) and
    /// a <see cref="NetworkCredential"/> when the original URI had
    /// user-info. Stripping at this seam means
    /// <see cref="WebProxy.Address"/>'s <c>UserInfo</c> can never be
    /// non-empty, so a stray <c>ToString()</c> / log of the proxy or
    /// handler state cannot leak the proxy password.
    /// </summary>
    internal static (Uri SanitisedAddress, NetworkCredential? Credentials) ParseProxyUri(string proxyUri)
    {
        // UriBuilder accepts the same input forms the historical
        // `new UriBuilder(proxyUri).Uri` call did (e.g.
        // "http://user:pass@proxy.local:3128"), so a CDP-injected
        // HTTP_PROXY value keeps working.
        var builder = new UriBuilder(proxyUri);
        NetworkCredential? credentials = null;
        if (!string.IsNullOrEmpty(builder.UserName) || !string.IsNullOrEmpty(builder.Password))
        {
            // Uri.UnescapeDataString matches the round-trip behaviour a
            // NetworkCredential consumer expects when the env-var
            // contains percent-encoded special characters.
            credentials = new NetworkCredential(
                Uri.UnescapeDataString(builder.UserName),
                Uri.UnescapeDataString(builder.Password));
            builder.UserName = string.Empty;
            builder.Password = string.Empty;
        }
        return (builder.Uri, credentials);
    }
}
