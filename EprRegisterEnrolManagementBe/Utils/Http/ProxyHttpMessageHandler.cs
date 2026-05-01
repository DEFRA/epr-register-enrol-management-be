using System.Net;
using Microsoft.Extensions.Hosting;

namespace EprRegisterEnrolManagementBe.Utils.Http;

public class ProxyHttpMessageHandler : HttpClientHandler
{
    public ProxyHttpMessageHandler(ILogger<ProxyHttpMessageHandler> logger, IHostEnvironment hostEnvironment)
        : this(
            logger,
            ResolveProxyOrFailClosed(logger, hostEnvironment),
            Environment.GetEnvironmentVariable("NO_PROXY")
                ?? Environment.GetEnvironmentVariable("no_proxy"))
    {
    }

    /// <summary>
    /// Resolve the proxy URI, failing closed in non-Development hosting
    /// environments (epr-cuq). docs/cdp-deployment.md requires
    /// HTTP(S)_PROXY for any external HTTP call outside Development —
    /// the historical warn-and-continue behaviour meant a misconfigured
    /// deployment would silently bypass the Squid proxy and egress
    /// outside the controlled network the first time a real downstream
    /// HTTP client was registered. Development still gets the
    /// warn-only path so local runs without proxy infrastructure work.
    /// </summary>
    internal static string? ResolveProxyOrFailClosed(
        ILogger<ProxyHttpMessageHandler> logger, IHostEnvironment hostEnvironment)
    {
        var resolved = ResolveProxyEnvironmentValue();
        if (resolved is not null)
        {
            return resolved;
        }

        if (hostEnvironment.IsDevelopment())
        {
            // Internal ctor logs the same warning when proxyUri is null,
            // so don't double-log here.
            return null;
        }

        throw new InvalidOperationException(
            "Outbound proxy is not configured: neither HTTPS_PROXY/HTTP_PROXY "
            + "(nor lower-case variants) is set. docs/cdp-deployment.md "
            + $"requires the CDP Squid proxy in '{hostEnvironment.EnvironmentName}'. "
            + "Set HTTPS_PROXY (and HTTP_PROXY where applicable) on the service "
            + "definition, or run with ASPNETCORE_ENVIRONMENT=Development for local "
            + "use without a proxy.");
    }

    /// <summary>
    /// Internal seam for tests (epr-9da, extended by epr-9g6, epr-cuq).
    /// Bypasses the fail-closed env-var check so tests can drive every
    /// proxyUri permutation deterministically without mutating
    /// process state.
    /// </summary>
    internal ProxyHttpMessageHandler(ILogger<ProxyHttpMessageHandler> logger, string? proxyUri, string? noProxy = null)
    {
        var proxy = new WebProxy { BypassProxyOnLocal = true };
        var bypassList = ParseNoProxy(noProxy);
        if (bypassList.Length > 0)
        {
            proxy.BypassList = bypassList;
        }

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
            logger.LogWarning("Neither HTTPS_PROXY nor HTTP_PROXY is set, proxy client will be disabled");
        }

        Proxy = proxy;
        UseProxy = proxyUri != null;
    }

    /// <summary>
    /// Resolve the proxy URI from the CDP-conventional environment
    /// variables (epr-9g6). HTTPS_PROXY takes precedence because all
    /// outbound traffic from this service is HTTPS; HTTP_PROXY is the
    /// fallback. Lower-case variants are checked too — they are the
    /// historical Unix convention and several CDP base images set them.
    /// Reading only HTTP_PROXY meant HTTPS egress would silently bypass
    /// the Squid proxy in environments where direct outbound is
    /// firewalled.
    /// </summary>
    internal static string? ResolveProxyEnvironmentValue() =>
        FirstNonEmpty(
            Environment.GetEnvironmentVariable("HTTPS_PROXY"),
            Environment.GetEnvironmentVariable("https_proxy"),
            Environment.GetEnvironmentVariable("HTTP_PROXY"),
            Environment.GetEnvironmentVariable("http_proxy"));

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (var value in candidates)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return null;
    }

    /// <summary>
    /// Parse a NO_PROXY value into a <see cref="WebProxy.BypassList"/>
    /// of regular expressions. The standard NO_PROXY convention is a
    /// comma-separated list of host suffixes (and optional ports) — we
    /// translate each entry into a URI regex so
    /// <c>internal.example</c> matches both <c>internal.example</c>
    /// itself and any subdomain. Whitespace and empty entries are
    /// tolerated. <c>*</c> bypasses everything.
    /// <para>
    /// <see cref="WebProxy.BypassList"/> entries are matched against the
    /// full request URI string (not just the host), so the patterns are
    /// scheme-prefixed and host-anchored.
    /// </para>
    /// </summary>
    internal static string[] ParseNoProxy(string? noProxy)
    {
        if (string.IsNullOrWhiteSpace(noProxy))
        {
            return Array.Empty<string>();
        }

        var parts = noProxy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var patterns = new List<string>(parts.Length);
        foreach (var raw in parts)
        {
            if (raw == "*")
            {
                patterns.Add(".*");
                continue;
            }
            // Strip a leading "." (".example.com" → "example.com") so
            // both forms match the same suffix.
            var entry = raw.StartsWith('.') ? raw[1..] : raw;
            if (entry.Length == 0)
            {
                continue;
            }
            // Match either "scheme://host[:port]/..." or
            // "scheme://*.host[:port]/..." so a NO_PROXY of
            // "internal.example" excludes "api.internal.example" too.
            patterns.Add(
                $@"^https?://(?:[^/]+\.)?{System.Text.RegularExpressions.Regex.Escape(entry)}(?::\d+)?(?:[/?#]|$)");
        }
        return patterns.ToArray();
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
