using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolManagementBe.Test.Integrations.OperatorBackend;

/// <summary>
/// RA-311/MBE-1: verifies the <c>OperatorBackendApi</c> configuration section
/// actually reaches <see cref="OperatorBackendApiConfig"/> through
/// <c>Program.cs</c>, and that the stub/real adapter selection follows
/// whether a URL is configured. Mirrors
/// <see cref="EprRegisterEnrolManagementBe.Test.Config.OperatorServiceConfigBindingTests"/>.
/// </summary>
public class OperatorBackendApiConfigBindingTests : IClassFixture<MongoIntegrationFixture>
{
    private readonly MongoIntegrationFixture _fixture;

    public OperatorBackendApiConfigBindingTests(MongoIntegrationFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public void Config_binds_from_the_OperatorBackendApi_section()
    {
        using var factory = NewFactory(
            url: "https://operator-backend.example.test",
            clientId: "custom-client-id",
            sharedSecret: "top-secret");

        var options = factory.Services.GetRequiredService<IOptions<OperatorBackendApiConfig>>();

        Assert.Equal("https://operator-backend.example.test", options.Value.Url);
        Assert.Equal("custom-client-id", options.Value.ClientId);
        Assert.Equal("top-secret", options.Value.SharedSecret);
    }

    [Fact]
    public void Url_is_empty_and_ClientId_defaults_when_the_section_is_not_set()
    {
        using var factory = NewFactory(url: null, clientId: null, sharedSecret: null);

        var options = factory.Services.GetRequiredService<IOptions<OperatorBackendApiConfig>>();

        Assert.Equal(string.Empty, options.Value.Url);
        Assert.Equal("epr-register-enrol-management-be", options.Value.ClientId);
        Assert.Null(options.Value.SharedSecret);
    }

    [Fact]
    public void NullAdapter_is_registered_when_url_is_not_configured()
    {
        using var factory = NewFactory(url: null, clientId: null, sharedSecret: null);

        var adapter = factory.Services.GetRequiredService<IOperatorBackendPushAdapter>();

        Assert.IsType<NullOperatorBackendPushAdapter>(adapter);
    }

    [Fact]
    public void HttpAdapter_is_registered_when_url_is_configured()
    {
        using var factory = NewFactory(
            url: "https://operator-backend.example.test", clientId: null, sharedSecret: null);

        var adapter = factory.Services.GetRequiredService<IOperatorBackendPushAdapter>();

        Assert.IsType<HttpOperatorBackendPushAdapter>(adapter);
    }

    // Url is always set explicitly (empty string when "unset") so an
    // ambient value in the test runner's environment cannot influence the
    // not-set case — same reasoning as OperatorServiceConfigBindingTests.
    // ClientId/SharedSecret are only added when a value is supplied: the
    // config binder treats an explicitly-empty key as "bind to empty
    // string", which would mask ClientId's non-empty default.
    private EphemeralMongoTestFactory NewFactory(string? url, string? clientId, string? sharedSecret)
    {
        var settings = new Dictionary<string, string?> { ["OperatorBackendApi:Url"] = url ?? string.Empty };
        if (clientId is not null)
        {
            settings["OperatorBackendApi:ClientId"] = clientId;
        }
        if (sharedSecret is not null)
        {
            settings["OperatorBackendApi:SharedSecret"] = sharedSecret;
        }

        return new(_fixture, "operator-backend-api-config", settings: settings);
    }
}
