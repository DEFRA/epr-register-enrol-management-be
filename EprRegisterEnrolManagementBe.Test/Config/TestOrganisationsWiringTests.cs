using EprRegisterEnrolManagementBe.Config;
using EprRegisterEnrolManagementBe.Test.Notifications;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx;
using Microsoft.Extensions.DependencyInjection;

namespace EprRegisterEnrolManagementBe.Test.Config;

/// <summary>
/// RA-000: boots the real Program.cs wiring to prove what
/// <c>TestOrganisations__Enabled</c> changes — the ReEx client registered,
/// and the prod startup guard. In the env-var collection because the guard
/// reads the process-global CDP <c>ENVIRONMENT</c> variable.
/// </summary>
[Collection(EnvVarMutationCollection.Name)]
public class TestOrganisationsWiringTests : IClassFixture<MongoIntegrationFixture>
{
    private readonly MongoIntegrationFixture _fixture;

    public TestOrganisationsWiringTests(MongoIntegrationFixture fixture) => _fixture = fixture;

    private EphemeralMongoTestFactory NewFactory(bool reExCredentials, bool? testOrganisations)
    {
        var settings = new Dictionary<string, string?>();
        if (reExCredentials)
        {
            settings["REEX_API_BASIC_AUTH_USERNAME"] = "reex-user";
            settings["REEX_API_BASIC_AUTH_PASSWORD"] = "reex-password";
            settings["ReExApi:BaseUrl"] = "https://reex.example.test";
        }
        if (testOrganisations is not null)
        {
            settings[TestOrganisations.EnabledKey] = testOrganisations.Value ? "true" : "false";
        }
        return new EphemeralMongoTestFactory(_fixture, "test-orgs", settings: settings);
    }

    [Fact]
    public void Real_reex_is_wrapped_so_e2e_organisations_reach_the_stub_when_enabled()
    {
        using var factory = NewFactory(reExCredentials: true, testOrganisations: true);

        Assert.IsType<TestOrganisationReExAccreditationClient>(
            factory.Services.GetRequiredService<IReExAccreditationClient>()
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void Real_reex_answers_everything_when_test_organisations_are_off(bool? testOrganisations)
    {
        using var factory = NewFactory(reExCredentials: true, testOrganisations);

        Assert.IsType<HttpReExAccreditationClient>(
            factory.Services.GetRequiredService<IReExAccreditationClient>()
        );
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_stub_answers_everything_without_reex_credentials(bool testOrganisations)
    {
        using var factory = NewFactory(reExCredentials: false, testOrganisations);

        Assert.IsType<StubReExAccreditationClient>(
            factory.Services.GetRequiredService<IReExAccreditationClient>()
        );
    }

    [Fact]
    public void Startup_fails_when_test_organisations_are_enabled_in_prod()
    {
        WithCdpEnvironment(
            "prod",
            () =>
            {
                using var factory = NewFactory(reExCredentials: false, testOrganisations: true);

                var ex = Assert.ThrowsAny<Exception>(() => factory.Services);
                Assert.Contains(
                    "TestOrganisations__Enabled",
                    ex.ToString(),
                    StringComparison.Ordinal
                );
            }
        );
    }

    [Fact]
    public void Startup_succeeds_in_prod_when_test_organisations_are_off()
    {
        WithCdpEnvironment(
            "prod",
            () =>
            {
                using var factory = NewFactory(reExCredentials: false, testOrganisations: null);

                Assert.NotNull(factory.Services.GetRequiredService<IReExAccreditationClient>());
            }
        );
    }

    private static void WithCdpEnvironment(string value, Action act)
    {
        var previous = Environment.GetEnvironmentVariable(CdpEnvironment.Variable);
        try
        {
            Environment.SetEnvironmentVariable(CdpEnvironment.Variable, value);
            act();
        }
        finally
        {
            Environment.SetEnvironmentVariable(CdpEnvironment.Variable, previous);
        }
    }
}
