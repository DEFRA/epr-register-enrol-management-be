using System.Text.Json;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-581 regression: endpoints such as prior-year deserialise the payload with
/// System.Text.Json, which threw on the nested <c>siteAddress</c> document that
/// form-created work items store (the prior-year section then failed to render).
/// </summary>
public class SiteAddressJsonConverterTests
{
    private static readonly JsonSerializerOptions s_options = new() { PropertyNameCaseInsensitive = true };

    private static ReAccreditationPayload Deserialise(string siteAddressJson) =>
        JsonSerializer.Deserialize<ReAccreditationPayload>(
            $$"""{ "organisationName": "Acme Ltd", "siteAddress": {{siteAddressJson}} }""",
            s_options)!;

    [Fact]
    public void Flat_string_is_read_as_is()
    {
        var payload = Deserialise("\"1 Main St, Leeds, LS1 1AB\"");

        Assert.Equal("1 Main St, Leeds, LS1 1AB", payload.SiteAddress);
    }

    [Fact]
    public void Nested_document_is_flattened_without_the_postcode()
    {
        var payload = Deserialise(
            """{ "line1": "12 Industrial Way", "line2": "Parkside Estate", "town": "Bristol", "postcode": "BS1 4DJ" }""");

        Assert.Equal("12 Industrial Way, Parkside Estate, Bristol", payload.SiteAddress);
        Assert.Equal("Acme Ltd", payload.OrganisationName);
    }

    [Fact]
    public void Nested_document_skips_blank_and_non_string_lines()
    {
        var payload = Deserialise("""{ "line1": "12 Industrial Way", "line2": " ", "town": 5 }""");

        Assert.Equal("12 Industrial Way", payload.SiteAddress);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"   \"")]
    [InlineData("{}")]
    [InlineData("""{ "postcode": "BS1 4DJ" }""")]
    [InlineData("42")]
    [InlineData("[1, 2]")]
    public void Absent_blank_or_unusable_values_read_as_null(string siteAddressJson)
    {
        var payload = Deserialise(siteAddressJson);

        Assert.Null(payload.SiteAddress);
        Assert.Equal("Acme Ltd", payload.OrganisationName);
    }

    [Fact]
    public void Round_trips_as_a_flat_string()
    {
        var json = JsonSerializer.Serialize(new ReAccreditationPayload { SiteAddress = "1 Main St, Leeds" }, s_options);

        Assert.Contains("\"SiteAddress\":\"1 Main St, Leeds\"", json);
    }
}
