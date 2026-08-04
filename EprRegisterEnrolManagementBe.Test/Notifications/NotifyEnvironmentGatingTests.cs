using EprRegisterEnrolManagementBe.Notifications;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace EprRegisterEnrolManagementBe.Test.Notifications;

/// <summary>
/// End-to-end DI assertion of the environment gate: even with a valid
/// NOTIFY_API_KEY, dev and localhost must resolve the non-dispatching
/// client so no send reaches Notify's guest-address limit and no failure
/// surfaces in the case-management UI.
///
/// <para>
/// <c>ENVIRONMENT</c> is set as a real process environment variable rather
/// than through <c>UseSetting</c>, for two reasons. It is how CDP actually
/// supplies it (unprefixed, so it lands in app configuration via
/// <c>AddEnvironmentVariables</c> when <c>WebApplication.CreateBuilder</c>
/// runs — early enough for <c>ConfigureNotifications</c> to read it). And
/// <c>ENVIRONMENT</c> is also <c>WebHostDefaults.EnvironmentKey</c>, so
/// passing it to <c>UseSetting</c> would silently rename the ASP.NET host
/// environment and entangle the two inputs this class exists to separate.
/// Mutating process state puts these tests in the non-parallel collection.
/// </para>
/// </summary>
[Collection(EnvVarMutationCollection.Name)]
public class NotifyEnvironmentGatingTests : IClassFixture<MongoIntegrationFixture>
{
    private readonly MongoIntegrationFixture _fixture;

    public NotifyEnvironmentGatingTests(MongoIntegrationFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("local")]
    [InlineData("dev")]
    public void NoOpNotifyClient_is_registered_in_non_sending_environments_despite_an_api_key(
        string environment
    )
    {
        AssertClientType<NoOpNotifyClient>(cdpEnvironment: environment, hostEnvironment: "Production");
    }

    [Fact]
    public void NoOpNotifyClient_is_registered_on_a_Development_host_despite_an_api_key()
    {
        AssertClientType<NoOpNotifyClient>(cdpEnvironment: null, hostEnvironment: "Development");
    }

    [Theory]
    [InlineData("test")]
    [InlineData("prod")]
    public void GovukNotifyClient_is_registered_in_sending_environments(string environment)
    {
        AssertClientType<GovukNotifyClient>(
            cdpEnvironment: environment,
            hostEnvironment: "Production"
        );
    }

    [Fact]
    public void Explicit_Notify_SendEmails_override_re_enables_sending_in_dev()
    {
        AssertClientType<GovukNotifyClient>(
            cdpEnvironment: "dev",
            hostEnvironment: "Development",
            sendEmails: "true"
        );
    }

    [Fact]
    public void Explicit_Notify_SendEmails_override_disables_sending_in_a_sending_environment()
    {
        AssertClientType<NoOpNotifyClient>(
            cdpEnvironment: "test",
            hostEnvironment: "Production",
            sendEmails: "false"
        );
    }

    private void AssertClientType<TExpected>(
        string? cdpEnvironment,
        string hostEnvironment,
        string? sendEmails = null
    )
    {
        var previous = Environment.GetEnvironmentVariable(
            NotifySendingPolicy.EnvironmentVariable
        );
        try
        {
            Environment.SetEnvironmentVariable(
                NotifySendingPolicy.EnvironmentVariable,
                cdpEnvironment
            );

            var settings = new Dictionary<string, string?>
            {
                ["NOTIFY_API_KEY"] = NotifyTestConstants.FakeApiKey,
            };
            if (sendEmails is not null)
            {
                settings[NotifySendingPolicy.SendEmailsKey] = sendEmails;
            }

            using var factory = new EphemeralMongoTestFactory(
                _fixture,
                "notify-env",
                environment: hostEnvironment,
                settings: settings
            );

            Assert.IsType<TExpected>(factory.Services.GetRequiredService<INotifyClient>());
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                NotifySendingPolicy.EnvironmentVariable,
                previous
            );
        }
    }
}
