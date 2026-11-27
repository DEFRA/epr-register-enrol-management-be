using EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.AddOverseasSite;

/// <summary>RA-698 (MBE-4): validation of the operator backend's resume-from-query body.</summary>
public sealed class AddOverseasSiteResumeValidatorTests
{
    private static readonly AddOverseasSiteResponder s_responder = new("Ada Operator", "ada@example.test", "Director");

    private static AddOverseasSiteResumeRequest Request(
        AddOverseasSiteResponder? responder = null,
        Dictionary<string, string?>? site = null,
        IReadOnlyList<AddOverseasSiteInterimSiteChange>? interimSites = null,
        IReadOnlyList<AddOverseasSiteBesFile>? besFiles = null,
        IReadOnlyList<string>? removedBesFileIds = null
    ) => new(responder ?? s_responder, DateTime.UtcNow, site, interimSites, besFiles, removedBesFileIds);

    private static AddOverseasSiteBesFile BesFile(string? fileId = "f-1", string? s3Key = "bes/f-1.pdf") =>
        new(fileId, "evidence.pdf", "application/pdf", s3Key, "bucket", "2026-01-01", "2027-01-01");

    [Fact]
    public void A_changed_site_field_is_valid() =>
        Assert.Null(AddOverseasSiteResumeValidator.Validate(Request(site: new() { ["siteName"] = "New name" })));

    [Fact]
    public void A_changed_interim_site_is_valid() =>
        Assert.Null(
            AddOverseasSiteResumeValidator.Validate(
                Request(interimSites: [new(7, "I-001", new() { ["postcode"] = "AB1 2CD" })])
            )
        );

    [Fact]
    public void A_new_bes_file_alone_is_valid() =>
        Assert.Null(AddOverseasSiteResumeValidator.Validate(Request(besFiles: [BesFile()])));

    [Fact]
    public void A_removed_bes_file_alone_is_valid() =>
        Assert.Null(AddOverseasSiteResumeValidator.Validate(Request(site: new(), removedBesFileIds: ["f-1"])));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void A_removed_bes_file_id_must_not_be_blank(string? id) =>
        Assert.Equal(
            AddOverseasSiteResumeValidator.RemovedBesFileIdInvalidMessage,
            AddOverseasSiteResumeValidator.Validate(Request(removedBesFileIds: [id!]))
        );

    [Fact]
    public void A_removed_bes_file_id_is_length_capped() =>
        Assert.Equal(
            AddOverseasSiteResumeValidator.RemovedBesFileIdInvalidMessage,
            AddOverseasSiteResumeValidator.Validate(
                Request(removedBesFileIds: [new string('x', AddOverseasSiteResumeRules.MaxValueLength + 1)])
            )
        );

    [Fact]
    public void A_response_that_changes_nothing_is_refused() =>
        Assert.Equal(
            AddOverseasSiteResumeValidator.NothingChangedMessage,
            AddOverseasSiteResumeValidator.Validate(Request(site: new(), interimSites: [], besFiles: []))
        );

    [Fact]
    public void A_missing_body_is_refused() =>
        Assert.Equal(AddOverseasSiteResumeValidator.MissingResponderMessage, AddOverseasSiteResumeValidator.Validate(null));

    [Theory]
    [InlineData(null, "a@example.test", "Director")]
    [InlineData("Ada", "", "Director")]
    [InlineData("Ada", "a@example.test", "  ")]
    public void The_responder_needs_a_name_email_and_role(string? name, string? email, string? role) =>
        Assert.Equal(
            AddOverseasSiteResumeValidator.MissingResponderMessage,
            AddOverseasSiteResumeValidator.Validate(
                Request(new AddOverseasSiteResponder(name, email, role), site: new() { ["siteName"] = "x" })
            )
        );

    [Theory]
    [InlineData("country")]
    [InlineData("latitude")]
    [InlineData("orsId")]
    [InlineData("operationCodes")]
    [InlineData("chargePence")]
    public void A_locked_or_unknown_site_field_is_refused(string field) =>
        Assert.Equal(
            AddOverseasSiteResumeValidator.UnknownSiteFieldMessage,
            AddOverseasSiteResumeValidator.Validate(Request(site: new() { [field] = "x" }))
        );

    [Theory]
    [InlineData("country")]
    [InlineData("operationCodes")]
    [InlineData("rejectedLoadsArrangements")]
    public void A_locked_interim_site_field_is_refused(string field) =>
        Assert.Equal(
            AddOverseasSiteResumeValidator.UnknownInterimSiteFieldMessage,
            AddOverseasSiteResumeValidator.Validate(Request(interimSites: [new(7, "I-001", new() { [field] = "x" })]))
        );

    [Fact]
    public void An_interim_site_change_must_name_its_site() =>
        Assert.Equal(
            AddOverseasSiteResumeValidator.InterimSiteNotIdentifiedMessage,
            AddOverseasSiteResumeValidator.Validate(
                Request(interimSites: [new(null, " ", new() { ["siteName"] = "x" })])
            )
        );

    [Fact]
    public void A_value_over_the_length_cap_is_refused() =>
        Assert.Equal(
            AddOverseasSiteResumeValidator.FieldTooLongMessage,
            AddOverseasSiteResumeValidator.Validate(
                Request(site: new() { ["siteName"] = new string('x', AddOverseasSiteResumeRules.MaxValueLength + 1) })
            )
        );

    [Theory]
    [InlineData(null, "bes/f-1.pdf")]
    [InlineData("f-1", null)]
    public void A_bes_file_needs_an_id_and_an_s3_key(string? fileId, string? s3Key) =>
        Assert.Equal(
            AddOverseasSiteResumeValidator.BesFileIncompleteMessage,
            AddOverseasSiteResumeValidator.Validate(Request(besFiles: [BesFile(fileId, s3Key)]))
        );
}
