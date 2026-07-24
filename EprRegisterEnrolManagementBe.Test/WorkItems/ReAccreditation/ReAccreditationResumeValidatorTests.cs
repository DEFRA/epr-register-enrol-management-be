using System.Text.Json;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-311/MBE-1: request validation for the resume-from-query endpoint.
/// Mirrors <see cref="ReAccreditationQueryValidatorTests"/>'s structure for
/// the section-key checks it shares with the raise side.
/// </summary>
public class ReAccreditationResumeValidatorTests
{
    private static ResumeFromQueryRequest Request(
        IReadOnlyList<string>? sectionKeys = null,
        ResponderContactDetails? responder = null,
        Dictionary<string, JsonElement>? sections = null) =>
        new(
            responder ?? new ResponderContactDetails("Jane Doe", "jane@example.com", "Manager"),
            sectionKeys ?? ["business-plan"],
            sections,
            FileReferences: null);

    // ------------------------------ sectionKeys ------------------------------

    [Fact]
    public void Validate_rejects_a_null_request()
    {
        Assert.Equal(
            ReAccreditationResumeValidator.NoSectionsMessage,
            ReAccreditationResumeValidator.Validate(null));
    }

    [Fact]
    public void Validate_rejects_null_section_keys()
    {
        // Constructed directly (not via the Request helper) because the
        // helper's own null-coalescing default would otherwise mask an
        // explicitly-null sectionKeys value.
        var request = new ResumeFromQueryRequest(
            new ResponderContactDetails("Jane Doe", "jane@example.com", "Manager"),
            SectionKeys: null,
            Sections: null,
            FileReferences: null);

        Assert.Equal(
            ReAccreditationResumeValidator.NoSectionsMessage,
            ReAccreditationResumeValidator.Validate(request));
    }

    [Fact]
    public void Validate_rejects_an_empty_section_keys_array()
    {
        Assert.Equal(
            ReAccreditationResumeValidator.NoSectionsMessage,
            ReAccreditationResumeValidator.Validate(Request(sectionKeys: [])));
    }

    [Theory]
    [InlineData("not-a-section")]
    [InlineData("")]
    [InlineData("Business-Plan")]
    public void Validate_rejects_an_unknown_section_key(string sectionKey)
    {
        Assert.Equal(
            ReAccreditationResumeValidator.UnknownSectionMessage,
            ReAccreditationResumeValidator.Validate(Request(sectionKeys: [sectionKey])));
    }

    [Theory]
    [InlineData("authority-to-issue")]
    [InlineData("business-plan")]
    [InlineData("prn-tonnage")]
    [InlineData("sampling-and-inspection-plan")]
    [InlineData("broadly-equivalent-standards")]
    [InlineData("overseas-reprocessing-sites")]
    public void Validate_accepts_each_of_the_six_known_sections(string sectionKey)
    {
        Assert.Null(ReAccreditationResumeValidator.Validate(Request(sectionKeys: [sectionKey])));
    }

    // -------------------------- responderContactDetails --------------------------

    [Theory]
    [InlineData(null, "jane@example.com", "Manager")]
    [InlineData("", "jane@example.com", "Manager")]
    [InlineData("Jane Doe", null, "Manager")]
    [InlineData("Jane Doe", "   ", "Manager")]
    [InlineData("Jane Doe", "jane@example.com", null)]
    [InlineData("Jane Doe", "jane@example.com", "")]
    public void Validate_rejects_incomplete_responder_details(
        string? fullName, string? email, string? role)
    {
        Assert.Equal(
            ReAccreditationResumeValidator.MissingResponderMessage,
            ReAccreditationResumeValidator.Validate(
                Request(responder: new ResponderContactDetails(fullName, email, role))));
    }

    [Fact]
    public void Validate_rejects_a_missing_responder()
    {
        // Constructed directly (not via the Request helper) because the
        // helper's own null-coalescing default would otherwise mask an
        // explicitly-null responder value.
        var request = new ResumeFromQueryRequest(
            ResponderContactDetails: null,
            SectionKeys: ["business-plan"],
            Sections: null,
            FileReferences: null);

        Assert.Equal(
            ReAccreditationResumeValidator.MissingResponderMessage,
            ReAccreditationResumeValidator.Validate(request));
    }

    // -------------------------------- sections --------------------------------

    [Fact]
    public void Validate_accepts_null_sections()
    {
        Assert.Null(ReAccreditationResumeValidator.Validate(Request(sections: null)));
    }

    [Fact]
    public void Validate_accepts_object_shaped_section_values()
    {
        var sections = new Dictionary<string, JsonElement>
        {
            ["business-plan"] = JsonDocument.Parse("""{"newInfrastructurePercent":20}""").RootElement,
        };

        Assert.Null(ReAccreditationResumeValidator.Validate(Request(sections: sections)));
    }

    [Fact]
    public void Validate_rejects_a_non_object_section_value()
    {
        var sections = new Dictionary<string, JsonElement>
        {
            ["business-plan"] = JsonDocument.Parse("\"not-an-object\"").RootElement,
        };

        Assert.Equal(
            ReAccreditationResumeValidator.InvalidSectionValueMessage,
            ReAccreditationResumeValidator.Validate(Request(sections: sections)));
    }
}
