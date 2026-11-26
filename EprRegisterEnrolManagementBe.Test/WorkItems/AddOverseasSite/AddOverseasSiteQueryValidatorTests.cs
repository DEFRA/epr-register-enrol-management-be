using EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.AddOverseasSite;

/// <summary>RA-698 (MBE-2): the add-ORS query body is the reason only.</summary>
public sealed class AddOverseasSiteQueryValidatorTests
{
    [Fact]
    public void A_reason_alone_is_valid() =>
        Assert.Null(AddOverseasSiteQueryValidator.Validate(new AddOverseasSiteQueryRequest("Please confirm the address")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_or_blank_reason_is_refused(string? reason) =>
        Assert.Equal(
            AddOverseasSiteQueryValidator.NoReasonMessage,
            AddOverseasSiteQueryValidator.Validate(new AddOverseasSiteQueryRequest(reason))
        );

    [Fact]
    public void A_missing_body_is_refused() =>
        Assert.Equal(AddOverseasSiteQueryValidator.NoReasonMessage, AddOverseasSiteQueryValidator.Validate(null));

    [Fact]
    public void Section_keys_are_not_accepted()
    {
        foreach (string[] sections in new string[][] { [], ["overseas-reprocessing-sites"] })
        {
            Assert.Equal(
                AddOverseasSiteQueryValidator.SectionsNotAcceptedMessage,
                AddOverseasSiteQueryValidator.Validate(new AddOverseasSiteQueryRequest("Why", sections))
            );
        }
    }

    [Fact]
    public void The_reason_is_capped_at_200_words()
    {
        var atCap = string.Join(' ', Enumerable.Repeat("word", 200));
        Assert.Null(AddOverseasSiteQueryValidator.Validate(new AddOverseasSiteQueryRequest(atCap)));
        Assert.Equal(
            AddOverseasSiteQueryValidator.ReasonTooLongMessage,
            AddOverseasSiteQueryValidator.Validate(new AddOverseasSiteQueryRequest(atCap + " more"))
        );
    }
}
