using EprRegisterEnrolManagementBe.Config;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolManagementBe.Test.Config;

/// <summary>
/// RA-291 (AC06): verifies OPERATOR_SERVICE_BASE_URL actually reaches
/// <see cref="OperatorServiceConfig"/> through Program.cs.
///
/// The notification hook's own tests inject <see cref="IOptions{T}"/>
/// directly, so they prove what the hook does with a base URL but never that
/// one is wired up. Without this, renaming the setting or breaking the
/// Configure call would leave every Queried email carrying an empty
/// operator_service_link with all other tests still green.
/// </summary>
public class OperatorServiceConfigBindingTests : IClassFixture<MongoIntegrationFixture>
{
    private readonly MongoIntegrationFixture _fixture;

    public OperatorServiceConfigBindingTests(MongoIntegrationFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public void BaseUrl_binds_from_OPERATOR_SERVICE_BASE_URL()
    {
        using var factory = NewFactory("https://operator.example.test");

        var options = factory.Services.GetRequiredService<IOptions<OperatorServiceConfig>>();

        Assert.Equal("https://operator.example.test", options.Value.BaseUrl);
    }

    [Fact]
    public void BaseUrl_is_empty_when_OPERATOR_SERVICE_BASE_URL_is_not_set()
    {
        using var factory = NewFactory(null);

        var options = factory.Services.GetRequiredService<IOptions<OperatorServiceConfig>>();

        Assert.Equal(string.Empty, options.Value.BaseUrl);
    }

    // Always set the key explicitly so an ambient value in the test runner's
    // environment cannot influence the not-set case — same reasoning as
    // NotifyApiKeyRegistrationTests.
    private EphemeralMongoTestFactory NewFactory(string? baseUrl) =>
        new(_fixture, "operator-service-config", settings: new Dictionary<string, string?>
        {
            ["OPERATOR_SERVICE_BASE_URL"] = baseUrl ?? string.Empty,
        });
}
