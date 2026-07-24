using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolManagementBe.Test.Integrations.OperatorBackend;

/// <summary>
/// RA-311/MBE-1: verifies the <c>OperatorBackendApi</c> configuration section
/// actually reaches <see cref="OperatorBackendApiConfig"/> through
/// <c>Program.cs</c>, and that the stub/real adapter selection follows the
/// explicit <c>Enabled</c> switch (MBE-F5) rather than whether a URL happens
/// to be set. Mirrors
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
            enabled: true,
            url: "https://operator-backend.example.test",
            clientId: "custom-client-id",
            sharedSecret: "top-secret");

        var options = factory.Services.GetRequiredService<IOptions<OperatorBackendApiConfig>>();

        Assert.True(options.Value.Enabled);
        Assert.Equal("https://operator-backend.example.test", options.Value.Url);
        Assert.Equal("custom-client-id", options.Value.ClientId);
        Assert.Equal("top-secret", options.Value.SharedSecret);
    }

    [Fact]
    public void Enabled_defaults_to_false_and_ClientId_defaults_when_the_section_is_not_set()
    {
        using var factory = NewFactory(enabled: null, url: null, clientId: null, sharedSecret: null);

        var options = factory.Services.GetRequiredService<IOptions<OperatorBackendApiConfig>>();

        Assert.False(options.Value.Enabled);
        Assert.Equal(string.Empty, options.Value.Url);
        Assert.Equal("epr-register-enrol-management-be", options.Value.ClientId);
        // appsettings.Development.json now carries an explicit blank
        // placeholder for SharedSecret (MBE-F3) rather than leaving the
        // section absent, so this binds to "" rather than the C# `null`
        // default — functionally identical (the adapter treats both as
        // "no secret configured" via string.IsNullOrEmpty).
        Assert.True(string.IsNullOrEmpty(options.Value.SharedSecret));
    }

    [Fact]
    public void NullAdapter_is_registered_when_disabled()
    {
        using var factory = NewFactory(enabled: false, url: null, clientId: null, sharedSecret: null);

        var adapter = factory.Services.GetRequiredService<IOperatorBackendPushAdapter>();

        Assert.IsType<NullOperatorBackendPushAdapter>(adapter);
    }

    [Fact]
    public void NullAdapter_is_registered_when_the_section_is_not_set_at_all()
    {
        // Enabled defaults to false, so an entirely-unset section must still
        // resolve to the no-op adapter, not throw and not select the real one.
        using var factory = NewFactory(enabled: null, url: null, clientId: null, sharedSecret: null);

        var adapter = factory.Services.GetRequiredService<IOperatorBackendPushAdapter>();

        Assert.IsType<NullOperatorBackendPushAdapter>(adapter);
    }

    [Fact]
    public void Disabled_warning_hosted_service_is_registered_when_disabled()
    {
        using var factory = NewFactory(enabled: false, url: null, clientId: null, sharedSecret: null);

        var hostedServices = factory.Services.GetServices<IHostedService>();

        Assert.Contains(hostedServices, s => s is OperatorBackendPushDisabledWarningHostedService);
    }

    [Fact]
    public void HttpAdapter_is_registered_when_enabled_with_complete_config()
    {
        using var factory = NewFactory(
            enabled: true,
            url: "https://operator-backend.example.test",
            clientId: "epr-register-enrol-management-be",
            sharedSecret: "top-secret");

        var adapter = factory.Services.GetRequiredService<IOperatorBackendPushAdapter>();

        Assert.IsType<HttpOperatorBackendPushAdapter>(adapter);
    }

    [Fact]
    public void Disabled_warning_hosted_service_is_not_registered_when_enabled()
    {
        using var factory = NewFactory(
            enabled: true,
            url: "https://operator-backend.example.test",
            clientId: "epr-register-enrol-management-be",
            sharedSecret: "top-secret");

        var hostedServices = factory.Services.GetServices<IHostedService>();

        Assert.DoesNotContain(hostedServices, s => s is OperatorBackendPushDisabledWarningHostedService);
    }

    [Fact]
    public void Startup_fails_when_enabled_but_url_is_missing()
    {
        using var factory = NewFactory(enabled: true, url: null, clientId: null, sharedSecret: "top-secret");

        var ex = Record.Exception(() => factory.Services);

        Assert.NotNull(ex);
        Assert.Contains("OperatorBackendApi", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_fails_when_enabled_but_shared_secret_is_missing()
    {
        using var factory = NewFactory(
            enabled: true, url: "https://operator-backend.example.test", clientId: null, sharedSecret: null);

        var ex = Record.Exception(() => factory.Services);

        Assert.NotNull(ex);
    }

    [Fact]
    public void Startup_succeeds_when_disabled_even_with_no_config_at_all()
    {
        // The behaviour-neutral-deploy guarantee MBE-F5 depends on: this
        // must never throw regardless of how incomplete the rest of the
        // section is, as long as Enabled stays false (or unset).
        using var factory = NewFactory(enabled: null, url: null, clientId: null, sharedSecret: null);

        var ex = Record.Exception(() => factory.Services);

        Assert.Null(ex);
    }

    // Url is always set explicitly (empty string when "unset") so an
    // ambient value in the test runner's environment cannot influence the
    // not-set case — same reasoning as OperatorServiceConfigBindingTests.
    // Enabled/ClientId/SharedSecret are only added when a value is
    // supplied: the config binder treats an explicitly-empty key as "bind
    // to empty string" (or "bind to false" for a bool), which would mask
    // ClientId's non-empty default and Enabled's false default.
    private EphemeralMongoTestFactory NewFactory(bool? enabled, string? url, string? clientId, string? sharedSecret)
    {
        var settings = new Dictionary<string, string?> { ["OperatorBackendApi:Url"] = url ?? string.Empty };
        if (enabled is not null)
        {
            settings["OperatorBackendApi:Enabled"] = enabled.Value ? "true" : "false";
        }
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
