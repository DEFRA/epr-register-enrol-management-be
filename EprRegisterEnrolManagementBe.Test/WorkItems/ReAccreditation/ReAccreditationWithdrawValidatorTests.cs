using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-252 request validation for the bespoke withdraw endpoint.
/// </summary>
public class ReAccreditationWithdrawValidatorTests
{
    [Fact]
    public void Validate_rejects_a_null_request()
    {
        Assert.Equal(
            ReAccreditationWithdrawValidator.MissingReasonMessage,
            ReAccreditationWithdrawValidator.Validate(null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n ")]
    public void Validate_rejects_a_missing_or_whitespace_reason(string? reason)
    {
        Assert.Equal(
            ReAccreditationWithdrawValidator.MissingReasonMessage,
            ReAccreditationWithdrawValidator.Validate(new WithdrawApplicationRequest(reason)));
    }

    [Fact]
    public void Validate_accepts_a_reason_of_exactly_two_hundred_words()
    {
        var reason = string.Join(' ', Enumerable.Repeat("word", 200));

        Assert.Null(ReAccreditationWithdrawValidator.Validate(new WithdrawApplicationRequest(reason)));
    }

    [Fact]
    public void Validate_rejects_a_reason_of_two_hundred_and_one_words()
    {
        var reason = string.Join(' ', Enumerable.Repeat("word", 201));

        Assert.Equal(
            ReAccreditationWithdrawValidator.ReasonTooLongMessage,
            ReAccreditationWithdrawValidator.Validate(new WithdrawApplicationRequest(reason)));
    }

    [Fact]
    public void Validate_accepts_a_short_valid_reason()
    {
        Assert.Null(ReAccreditationWithdrawValidator.Validate(
            new WithdrawApplicationRequest("The application no longer meets our needs.")));
    }

    [Fact]
    public void MaxReasonWords_matches_the_query_validators_cap()
    {
        Assert.Equal(ReAccreditationQueryValidator.MaxReasonWords, ReAccreditationWithdrawValidator.MaxReasonWords);
    }
}
