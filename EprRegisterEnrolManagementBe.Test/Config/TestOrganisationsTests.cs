using EprRegisterEnrolManagementBe.Config;
using Microsoft.Extensions.Configuration;

namespace EprRegisterEnrolManagementBe.Test.Config;

public class TestOrganisationsTests
{
    private static IConfiguration ConfigWith(string? enabled) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                enabled is null
                    ? new Dictionary<string, string?>()
                    : new Dictionary<string, string?> { [TestOrganisations.EnabledKey] = enabled }
            )
            .Build();

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    public void IsEnabled_defaults_to_false_and_reads_the_switch(string? value, bool expected)
    {
        Assert.Equal(expected, TestOrganisations.IsEnabled(ConfigWith(value)));
    }

    [Theory]
    [InlineData("e2e-3f2a9c", true)]
    [InlineData("e2e-", true)]
    [InlineData("E2E-3f2a9c", false)]
    [InlineData("xe2e-3f2a9c", false)]
    [InlineData("e2e3f2a9c", false)]
    [InlineData("500027", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTestOrganisation_matches_only_the_lowercase_e2e_prefix(
        string? organisationId,
        bool expected
    )
    {
        Assert.Equal(expected, TestOrganisations.IsTestOrganisation(organisationId));
    }

    [Theory]
    [InlineData("prod")]
    [InlineData("PROD")]
    [InlineData(" prod ")]
    public void EnsureNotEnabledInProd_throws_when_enabled_in_prod(string cdpEnvironment)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TestOrganisations.EnsureNotEnabledInProd(ConfigWith("true"), cdpEnvironment)
        );
        Assert.Contains("TestOrganisations__Enabled", ex.Message);
    }

    [Theory]
    [InlineData("test")]
    [InlineData("dev")]
    [InlineData("perf-test")]
    [InlineData("ext-test")]
    [InlineData("production")]
    [InlineData(null)]
    public void EnsureNotEnabledInProd_allows_enabled_outside_prod(string? cdpEnvironment)
    {
        TestOrganisations.EnsureNotEnabledInProd(ConfigWith("true"), cdpEnvironment);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void EnsureNotEnabledInProd_allows_prod_when_disabled(string? enabled)
    {
        TestOrganisations.EnsureNotEnabledInProd(ConfigWith(enabled), "prod");
    }
}
