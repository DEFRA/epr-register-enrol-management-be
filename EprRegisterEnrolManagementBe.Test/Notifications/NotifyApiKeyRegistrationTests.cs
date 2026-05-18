using EprRegisterEnrolManagementBe.Notifications;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace EprRegisterEnrolManagementBe.Test.Notifications;

/// <summary>
/// Verifies that ConfigureNotifications registers the correct INotifyClient
/// implementation depending on whether a Notify API key is available.
///
/// Config-key tests use UseSetting and are safe for parallel execution.
/// Env-var tests mutate the process environment and are placed in the
/// env-var-mutation collection (DisableParallelization = true) to prevent
/// races with other WebApplicationFactory spin-ups — same reasoning as
/// WorkItemSeederGatingTests.
/// </summary>
public class NotifyApiKeyRegistrationTests
{
    // The Notify SDK's NotificationClient constructor extracts a service-id
    // and API-key via Substring(len - 73, 36) and Substring(len - 36), so
    // the key must be >= 73 chars. The value below is intentionally not in
    // real key format so secret scanners do not flag it.
    private const string FakeApiKey =
        "not-a-real-notify-key-for-unit-tests-only-do-not-use-aaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void GovukNotifyClient_is_registered_when_ApiKey_config_is_set()
    {
        using var factory = new NotifyTestFactory(apiKey: FakeApiKey);

        var client = factory.Services.GetRequiredService<INotifyClient>();

        Assert.IsType<GovukNotifyClient>(client);
    }

    [Fact]
    public void NoOpNotifyClient_is_registered_when_ApiKey_config_is_absent()
    {
        using var factory = new NotifyTestFactory(apiKey: null);

        var client = factory.Services.GetRequiredService<INotifyClient>();

        Assert.IsType<NoOpNotifyClient>(client);
    }

    [Fact]
    public void NoOpNotifyClient_is_registered_when_ApiKey_config_is_empty()
    {
        using var factory = new NotifyTestFactory(apiKey: "");

        var client = factory.Services.GetRequiredService<INotifyClient>();

        Assert.IsType<NoOpNotifyClient>(client);
    }

    private sealed class NotifyTestFactory(string? apiKey) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            if (apiKey is not null)
                builder.UseSetting("Notify:ApiKey", apiKey);
        }
    }
}

/// <summary>
/// Collection that disables parallelization for tests that mutate
/// process-global environment variables.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvVarMutationCollection
{
    public const string Name = "env-var-mutation";
}

/// <summary>
/// Verifies that the NOTIFY_API_KEY environment variable is picked up by
/// Program.cs and drives the same INotifyClient registration decision.
/// </summary>
[Collection(EnvVarMutationCollection.Name)]
public class NotifyApiKeyEnvVarRegistrationTests
{
    private const string FakeApiKey =
        "not-a-real-notify-key-for-unit-tests-only-do-not-use-aaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void GovukNotifyClient_is_registered_when_NOTIFY_API_KEY_env_var_is_set()
    {
        var previous = Environment.GetEnvironmentVariable("NOTIFY_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("NOTIFY_API_KEY", FakeApiKey);

            using var factory = new WebApplicationFactory<Program>();
            Assert.IsType<GovukNotifyClient>(factory.Services.GetRequiredService<INotifyClient>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOTIFY_API_KEY", previous);
        }
    }

    [Fact]
    public void NoOpNotifyClient_is_registered_when_NOTIFY_API_KEY_env_var_is_absent()
    {
        var previous = Environment.GetEnvironmentVariable("NOTIFY_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("NOTIFY_API_KEY", null);

            using var factory = new WebApplicationFactory<Program>();
            Assert.IsType<NoOpNotifyClient>(factory.Services.GetRequiredService<INotifyClient>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOTIFY_API_KEY", previous);
        }
    }
}
