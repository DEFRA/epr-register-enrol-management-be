using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-294/RA-297: request validation for the bespoke site-added notification
/// endpoint.
/// </summary>
public class ReAccreditationSiteAddedValidatorTests
{
    [Fact]
    public void Validate_rejects_a_null_request()
    {
        Assert.Equal(
            ReAccreditationSiteAddedValidator.InvalidSiteTypeMessage,
            ReAccreditationSiteAddedValidator.Validate(null)
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ORS")]
    [InlineData("Interim")]
    [InlineData("something-else")]
    public void Validate_rejects_an_unknown_site_type(string? siteType)
    {
        var request = new SiteAddedRequest(siteType, "001", null, true);

        Assert.Equal(
            ReAccreditationSiteAddedValidator.InvalidSiteTypeMessage,
            ReAccreditationSiteAddedValidator.Validate(request)
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_rejects_a_missing_ors_id_for_an_ors_site(string? orsId)
    {
        var request = new SiteAddedRequest("ors", orsId, null, true);

        Assert.Equal(
            ReAccreditationSiteAddedValidator.MissingOrsIdMessage,
            ReAccreditationSiteAddedValidator.Validate(request)
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_rejects_a_missing_ors_id_for_an_interim_site(string? orsId)
    {
        var request = new SiteAddedRequest("interim", orsId, "INT-1", true);

        Assert.Equal(
            ReAccreditationSiteAddedValidator.MissingOrsIdMessage,
            ReAccreditationSiteAddedValidator.Validate(request)
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_rejects_a_missing_site_number_for_an_interim_site(string? siteNumber)
    {
        var request = new SiteAddedRequest("interim", "001", siteNumber, true);

        Assert.Equal(
            ReAccreditationSiteAddedValidator.MissingSiteNumberMessage,
            ReAccreditationSiteAddedValidator.Validate(request)
        );
    }

    [Fact]
    public void Validate_accepts_an_ors_site_with_a_null_site_number()
    {
        var request = new SiteAddedRequest("ors", "001", null, true);

        Assert.Null(ReAccreditationSiteAddedValidator.Validate(request));
    }

    [Fact]
    public void Validate_does_not_require_a_site_number_be_absent_for_an_ors_site()
    {
        // The contract example always sends siteNumber: null for "ors", but
        // the validator does not police that — only that an "interim" site
        // supplies one.
        var request = new SiteAddedRequest("ors", "001", "unexpected-but-harmless", true);

        Assert.Null(ReAccreditationSiteAddedValidator.Validate(request));
    }

    [Fact]
    public void Validate_accepts_an_interim_site_with_a_site_number()
    {
        var request = new SiteAddedRequest("interim", "001", "INT-1", true);

        Assert.Null(ReAccreditationSiteAddedValidator.Validate(request));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Validate_accepts_either_isNewSite_value(bool isNewSite)
    {
        var request = new SiteAddedRequest("ors", "001", null, isNewSite);

        Assert.Null(ReAccreditationSiteAddedValidator.Validate(request));
    }
}
