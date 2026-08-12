using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-291: request validation for the bespoke query endpoint. The word-count
/// rule is a shared contract with the case management frontend, so it gets
/// its own boundary tests at exactly 200 / 201 words.
/// </summary>
public class ReAccreditationQueryValidatorTests
{
    private static QueryApplicationRequest Request(
        IReadOnlyList<string>? sections = null,
        string? reason = "Please clarify the tonnage figures.") =>
        new(sections ?? ["business-plan"], reason);

    // ------------------------------ sections ------------------------------

    [Fact]
    public void Validate_rejects_a_null_request()
    {
        Assert.Equal(
            ReAccreditationQueryValidator.NoSectionsMessage,
            ReAccreditationQueryValidator.Validate(null));
    }

    [Fact]
    public void Validate_rejects_null_sections()
    {
        Assert.Equal(
            ReAccreditationQueryValidator.NoSectionsMessage,
            ReAccreditationQueryValidator.Validate(new QueryApplicationRequest(null, "why")));
    }

    [Fact]
    public void Validate_rejects_an_empty_sections_array()
    {
        Assert.Equal(
            ReAccreditationQueryValidator.NoSectionsMessage,
            ReAccreditationQueryValidator.Validate(new QueryApplicationRequest([], "why")));
    }

    [Theory]
    [InlineData("not-a-section")]
    [InlineData("")]
    // Section ids are matched ordinally: casing variants are not the
    // frontend's ids and must be rejected.
    [InlineData("Business-Plan")]
    public void Validate_rejects_an_unknown_section_id(string section)
    {
        Assert.Equal(
            ReAccreditationQueryValidator.UnknownSectionMessage,
            ReAccreditationQueryValidator.Validate(Request(sections: [section])));
    }

    [Fact]
    public void Validate_rejects_a_null_section_id()
    {
        Assert.Equal(
            ReAccreditationQueryValidator.UnknownSectionMessage,
            ReAccreditationQueryValidator.Validate(Request(sections: ["business-plan", null!])));
    }

    [Theory]
    [InlineData("authority-to-issue")]
    [InlineData("business-plan")]
    [InlineData("prn-tonnage")]
    [InlineData("sampling-and-inspection-plan")]
    [InlineData("broadly-equivalent-standards")]
    [InlineData("overseas-reprocessing-sites")]
    public void Validate_accepts_each_of_the_six_known_sections(string section)
    {
        Assert.Null(ReAccreditationQueryValidator.Validate(Request(sections: [section])));
    }

    [Fact]
    public void Validate_accepts_all_six_sections_at_once()
    {
        Assert.Null(ReAccreditationQueryValidator.Validate(
            Request(sections: [.. ReAccreditationQuerySections.All])));
    }

    // ------------------------------- reason -------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n ")]
    public void Validate_rejects_a_missing_or_whitespace_reason(string? reason)
    {
        Assert.Equal(
            ReAccreditationQueryValidator.MissingReasonMessage,
            ReAccreditationQueryValidator.Validate(Request(reason: reason)));
    }

    [Fact]
    public void Validate_accepts_a_reason_of_exactly_two_hundred_words()
    {
        var reason = string.Join(' ', Enumerable.Repeat("word", 200));

        Assert.Null(ReAccreditationQueryValidator.Validate(Request(reason: reason)));
    }

    [Fact]
    public void Validate_rejects_a_reason_of_two_hundred_and_one_words()
    {
        var reason = string.Join(' ', Enumerable.Repeat("word", 201));

        Assert.Equal(
            ReAccreditationQueryValidator.ReasonTooLongMessage,
            ReAccreditationQueryValidator.Validate(Request(reason: reason)));
    }

    // ---------------------------- word counting ----------------------------

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("one", 1)]
    [InlineData("  one  ", 1)]
    [InlineData("one two three", 3)]
    // Runs of whitespace collapse to a single separator, matching a \s+ split.
    [InlineData("one    two", 2)]
    [InlineData("one\ttwo\nthree\r\nfour", 4)]
    [InlineData("  leading and trailing  ", 3)]
    // Punctuation does not split words — only whitespace does.
    [InlineData("well-formed, isn't it?", 3)]
    public void CountWords_matches_the_frontend_whitespace_split(string? input, int expected)
    {
        Assert.Equal(expected, QueryReasonWordCounter.CountWords(input));
    }
}
