using EprRegisterEnrolManagementBe.Config;

namespace EprRegisterEnrolManagementBe.Test.Config;

public class CdpEnvironmentTests
{
    [Theory]
    [InlineData("prod", true)]
    [InlineData("Prod", true)]
    [InlineData(" prod\t", true)]
    [InlineData("test", false)]
    [InlineData("production", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsProd_recognises_only_the_prod_environment(string? cdpEnvironment, bool expected)
    {
        Assert.Equal(expected, CdpEnvironment.IsProd(cdpEnvironment));
    }
}
