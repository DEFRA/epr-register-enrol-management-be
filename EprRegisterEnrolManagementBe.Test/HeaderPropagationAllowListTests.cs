using EprRegisterEnrolManagementBe.Test.TestSupport;
using Microsoft.AspNetCore.HeaderPropagation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolManagementBe.Test;

/// <summary>
/// Uses <see cref="EphemeralMongoTestFactory"/> so the host boots against
/// the shared assembly-fixture ephemeral mongod instead of the default,
/// unreachable-in-tests connection string — that otherwise left
/// WorkItemPersistence's startup index reconciliation to eat a ~90s Mongo
/// server-selection timeout per test, even though this test never touches
/// Mongo.
/// </summary>
public class HeaderPropagationAllowListTests
{
    private readonly MongoIntegrationFixture _fixture;

    public HeaderPropagationAllowListTests(MongoIntegrationFixture fixture) => _fixture = fixture;

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
        "x-cdp-auth-timestamp",
        "x-cdp-auth-nonce",
        "x-api-key",
        "x-cdp-user-id",
        "x-cdp-user-name",
        "x-cdp-user-roles",
        "x-cdp-cognito-client-id",
    };

    [Theory]
    [MemberData(nameof(ForbiddenHeaders))]
    public async Task Allow_list_does_not_include_credential_or_identity_headers(string header)
    {
        // epr-6e5: WebApplicationFactory implements IAsyncDisposable;
        // a 'using var' on a sync void test only invokes Dispose(),
        // skipping the async teardown path the host registers for
        // its IHostedService graph. Use 'await using' so the kestrel
        // pipeline / hosted services are torn down on their async
        // path.
        await using var factory = new EphemeralMongoTestFactory(_fixture, "header-prop");
        var options = factory.Services
            .GetRequiredService<IOptions<HeaderPropagationOptions>>().Value;

        Assert.DoesNotContain(options.Headers,
            h => string.Equals(h.CapturedHeaderName, header, StringComparison.OrdinalIgnoreCase));
    }
}