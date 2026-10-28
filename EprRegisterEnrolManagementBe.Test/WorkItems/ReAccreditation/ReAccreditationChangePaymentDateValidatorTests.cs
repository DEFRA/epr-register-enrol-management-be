using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-576 cover for <see cref="ReAccreditationChangePaymentDateValidator"/>.
/// The error codes and fields are a wire contract with management-fe.
/// </summary>
public class ReAccreditationChangePaymentDateValidatorTests
{
    private static readonly DateOnly s_today = new(2026, 8, 11);

    [Fact]
    public void A_valid_date_and_reason_succeed_with_the_reason_trimmed()
    {
        var result = ReAccreditationChangePaymentDateValidator.Validate(
            "2026-07-20",
            "  Wrong date entered.  ",
            s_today
        );

        Assert.True(result.IsValid);
        Assert.Equal(new DateOnly(2026, 7, 20), result.PaymentDate);
        Assert.Equal("Wrong date entered.", result.Reason);
    }

    [Theory]
    [InlineData(null, "payment-date-required")]
    [InlineData("not-a-date", "payment-date-invalid")]
    [InlineData("2026-08-12", "payment-date-in-future")]
    [InlineData("2025-01-01", "payment-date-too-old")]
    public void Date_failures_reuse_the_duly_making_codes_against_the_date_field(
        string? date,
        string expectedCode
    )
    {
        var result = ReAccreditationChangePaymentDateValidator.Validate(date, "A reason", s_today);

        Assert.False(result.IsValid);
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.Equal("paymentDate", result.Field);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_reason_is_rejected(string? reason)
    {
        var result = ReAccreditationChangePaymentDateValidator.Validate("2026-07-20", reason, s_today);

        Assert.Equal("payment-date-reason-required", result.ErrorCode);
        Assert.Equal("reason", result.Field);
    }

    [Fact]
    public void A_reason_of_exactly_200_words_is_accepted_and_201_is_rejected()
    {
        var atLimit = string.Join(' ', Enumerable.Repeat("word", 200));
        var overLimit = atLimit + " word";

        Assert.True(
            ReAccreditationChangePaymentDateValidator.Validate("2026-07-20", atLimit, s_today).IsValid
        );

        var result = ReAccreditationChangePaymentDateValidator.Validate(
            "2026-07-20",
            overLimit,
            s_today
        );
        Assert.Equal("payment-date-reason-too-long", result.ErrorCode);
        Assert.Equal("reason", result.Field);
    }

    [Fact]
    public void Words_are_counted_across_any_whitespace_including_newlines()
    {
        var reason = string.Join("\r\n", Enumerable.Repeat("word", 201));

        var result = ReAccreditationChangePaymentDateValidator.Validate("2026-07-20", reason, s_today);

        Assert.Equal("payment-date-reason-too-long", result.ErrorCode);
    }

    [Fact]
    public void A_date_error_takes_precedence_over_a_reason_error()
    {
        var result = ReAccreditationChangePaymentDateValidator.Validate("bad", null, s_today);

        Assert.Equal("payment-date-invalid", result.ErrorCode);
    }
}
