using System.Diagnostics.CodeAnalysis;

namespace EprRegisterEnrolManagementBe.Utils.Http;

[ExcludeFromCodeCoverage]
public static class HttpClientRegistrationExtension
{
    public static IHttpClientBuilder AddHttpClientWithTracing<TClient, TImplementation>(
        this IServiceCollection services)
        where TClient : class
        where TImplementation : class, TClient
    {
        services.AddTransient<ProxyHttpMessageHandler>();

        // UseProxy = false is explicit, not the framework default: this is
        // the "unproxied" counterpart to AddHttpClientWithProxy below, so it
        // must not silently inherit HttpClient.DefaultProxy (set process-wide
        // in Program.cs for the Notify SDK's bare HttpClient) just because no
        // primary handler was configured. RA-311 query-push-proxy-fix hit
        // exactly this for "DefaultClient" — omitting a handler here isn't
        // "unproxied", it's "whatever DefaultProxy happens to be".
        return services
            .AddHttpClient<TClient, TImplementation>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseProxy = false })
            .AddHeaderPropagation();
    }

    public static IHttpClientBuilder AddHttpClientWithProxy<TClient, TImplementation>(
        this IServiceCollection services)
        where TClient : class
        where TImplementation : class, TClient
    {
        services.AddTransient<ProxyHttpMessageHandler>();

        return services
            .AddHttpClient<TClient, TImplementation>()
            .AddHeaderPropagation()
            .ConfigurePrimaryHttpMessageHandler<ProxyHttpMessageHandler>();
    }
}