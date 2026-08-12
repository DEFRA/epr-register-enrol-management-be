using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-316 payment-date validation. The error codes asserted here are a wire
/// contract with management-fe (which maps each to user-facing copy for the
/// GOV.UK error summary) and with the mgmt-tests e2e suite, so these assertions
/// are deliberately on the literal code strings rather than on an enum — a
/// rename that compiled cleanly would still break both consumers.
/// </summary>
public class ReAccreditationDulyMakingValidatorTests
{
    private static readonly DateOnly s_today = new(2026, 8, 11);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_date_is_required(string? input)
    {
        var result = ReAccreditationDulyMakingValidator.Validate(input, s_today);

        Assert.False(result.IsValid);
        Assert.Equal("payment-date-required", result.ErrorCode);
        Assert.Null(result.PaymentDate);
    }

    [Theory]
    // Not a date at all.
    [InlineData("not-a-date")]
    // Real-looking but impossible.
    [InlineData("2026-02-30")]
    [InlineData("2026-13-01")]
    // Right day, wrong format — the frontend must never send these.
    [InlineData("11/08/2026")]
    [InlineData("2026-8-11")]
    [InlineData("11-08-2026")]
    // An ISO timestamp is explicitly rejected: the contract is a plain date, and
    // accepting a timestamp would invite a timezone-shifted SLA anchor.
    [InlineData("2026-08-11T00:00:00Z")]
    [InlineData("2026-08-11T13:45:00+01:00")]
    public void An_unparseable_date_is_invalid(string input)
    {
        var result = ReAccreditationDulyMakingValidator.Validate(input, s_today);

        Assert.False(result.IsValid);
        Assert.Equal("payment-date-invalid", result.ErrorCode);
    }

    [Fact]
    public void A_future_date_is_rejected()
    {
        var result = ReAccreditationDulyMakingValidator.Validate("2026-08-12", s_today);

        Assert.False(result.IsValid);
        Assert.Equal("payment-date-in-future", result.ErrorCode);
    }

    /// <summary>
    /// The boundary the frontend writes its copy around: today passes, tomorrow
    /// fails. A regulator recording a payment made this morning must not be
    /// turned away.
    /// </summary>
    [Fact]
    public void Today_is_accepted()
    {
        var result = ReAccreditationDulyMakingValidator.Validate("2026-08-11", s_today);

        Assert.True(result.IsValid);
        Assert.Equal(s_today, result.PaymentDate);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void A_date_more_than_twelve_months_old_is_rejected()
    {
        // 366 days before today.
        var result = ReAccreditationDulyMakingValidator.Validate("2025-08-10", s_today);

        Assert.False(result.IsValid);
        Assert.Equal("payment-date-too-old", result.ErrorCode);
    }

    [Fact]
    public void The_twelve_month_floor_itself_is_accepted()
    {
        // Exactly 365 days before today — the earliest accepted date.
        var result = ReAccreditationDulyMakingValidator.Validate("2025-08-11", s_today);

        Assert.True(result.IsValid);
        Assert.Equal(new DateOnly(2025, 8, 11), result.PaymentDate);
    }

    /// <summary>
    /// The decision recorded in RA-316: a payment date EARLIER than the
    /// application's submission date is accepted. A regulator recording a
    /// payment that genuinely landed before the application reached case
    /// management is a real case, and refusing it would strand the application
    /// with no workaround. The validator therefore never sees the submission
    /// date at all — the only past bound is the twelve-month floor.
    /// </summary>
    [Fact]
    public void A_date_before_the_application_was_submitted_is_accepted()
    {
        var result = ReAccreditationDulyMakingValidator.Validate("2026-01-05", s_today);

        Assert.True(result.IsValid);
        Assert.Equal(new DateOnly(2026, 1, 5), result.PaymentDate);
    }

    [Fact]
    public void Surrounding_whitespace_is_tolerated()
    {
        var result = ReAccreditationDulyMakingValidator.Validate("  2026-08-01  ", s_today);

        Assert.True(result.IsValid);
        Assert.Equal(new DateOnly(2026, 8, 1), result.PaymentDate);
    }
}
