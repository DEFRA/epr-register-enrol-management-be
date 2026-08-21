using EprRegisterEnrolManagementBe.Notifications;

namespace EprRegisterEnrolManagementBe.Test.Notifications;

/// <summary>
/// The environment gate that decides whether Notify sends are really
/// dispatched. The non-production Notify team key can only reach registered
/// team addresses plus five guests, so dev and localhost must never dispatch;
/// test and prod must always continue to.
/// </summary>
public class NotifySendingPolicyTests
{
    [Theory]
    [InlineData("local")]
    [InlineData("dev")]
    [InlineData("DEV")]
    [InlineData(" dev ")]
    public void Sending_is_off_for_non_sending_environments(string environment)
    {
        Assert.False(
            NotifySendingPolicy.ShouldSendEmails(
                explicitOverride: null,
                cdpEnvironment: environment,
                isDevelopmentHostEnvironment: false
            )
        );
    }

    [Theory]
    [InlineData("test")]
    [InlineData("ext-test")]
    [InlineData("perf-test")]
    [InlineData("prod")]
    public void Sending_is_on_for_deployed_sending_environments(string environment)
    {
        Assert.True(
            NotifySendingPolicy.ShouldSendEmails(
                explicitOverride: null,
                cdpEnvironment: environment,
                isDevelopmentHostEnvironment: false
            )
        );
    }

    // The Compose stack and `dotnet run` do not set ENVIRONMENT; the
    // ASP.NET host environment is what identifies a developer machine.
    [Fact]
    public void Sending_is_off_on_a_Development_host_with_no_ENVIRONMENT()
    {
        Assert.False(
            NotifySendingPolicy.ShouldSendEmails(
                explicitOverride: null,
                cdpEnvironment: null,
                isDevelopmentHostEnvironment: true
            )
        );
    }

    // Fail-open: a deployed environment whose ENVIRONMENT is unset or renamed
    // must keep sending rather than silently swallow every notification.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("something-new")]
    public void Sending_is_on_when_ENVIRONMENT_is_unset_or_unrecognised(string? environment)
    {
        Assert.True(
            NotifySendingPolicy.ShouldSendEmails(
                explicitOverride: null,
                cdpEnvironment: environment,
                isDevelopmentHostEnvironment: false
            )
        );
    }

    [Fact]
    public void Explicit_override_can_force_sending_on_in_a_non_sending_environment()
    {
        Assert.True(
            NotifySendingPolicy.ShouldSendEmails(
                explicitOverride: true,
                cdpEnvironment: "dev",
                isDevelopmentHostEnvironment: true
            )
        );
    }

    [Fact]
    public void Explicit_override_can_force_sending_off_in_a_sending_environment()
    {
        Assert.False(
            NotifySendingPolicy.ShouldSendEmails(
                explicitOverride: false,
                cdpEnvironment: "prod",
                isDevelopmentHostEnvironment: false
            )
        );
    }
}
