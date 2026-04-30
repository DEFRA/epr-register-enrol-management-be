using Microsoft.AspNetCore.HeaderPropagation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolManagementBe.Test;

public class HeaderPropagationAllowListTests
{
    /// <summary>
    /// Headers that MUST never be on the propagation allow-list. If any of
    /// these slip through, an outbound HTTP call could replay caller
    /// credentials, trust headers, or identity assertions to a downstream
    /// service that has no business seeing them.
    /// </summary>
    public static readonly TheoryData<string> ForbiddenHeaders = new()
    {
        "Authorization",
        "Cookie",
        "x-cdp-auth-signature",
        "x-api-key",
        "x-cdp-user-id",
        "x-cdp-user-name",
        "x-cdp-user-roles",
        "x-cdp-cognito-client-id",
    };

    [Theory]
    [MemberData(nameof(ForbiddenHeaders))]
    public void Allow_list_does_not_include_credential_or_identity_headers(string header)
    {
        using var factory = new WebApplicationFactory<Program>();
        var options = factory.Services
            .GetRequiredService<IOptions<HeaderPropagationOptions>>().Value;

        Assert.DoesNotContain(options.Headers,
            h => string.Equals(h.CapturedHeaderName, header, StringComparison.OrdinalIgnoreCase));
    }
}