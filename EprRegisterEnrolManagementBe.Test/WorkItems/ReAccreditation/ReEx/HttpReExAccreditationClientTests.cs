using System.Net;
using System.Text;
using EprRegisterEnrolManagementBe.Utils.Logging;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation.ReEx;

/// <summary>
/// ReEx returns tonnage band as a raw snake_case code (e.g. "up_to_500"),
/// not the canonical "UpTo500"-style value the rest of the app uses (see
/// epr-register-enrol-backend's HttpReExApiAdapter.TonnageBandMap, which
/// normalises the same raw codes for the operator side). Without mapping it
/// here too, the Application details page's prior-year section renders the
/// raw ReEx code unformatted instead of a readable label — only visible
/// against the real ReEx API, since the local/CI stub client already
/// returns a canonical value.
/// </summary>
public class HttpReExAccreditationClientTests
{
    private static HttpReExAccreditationClient CreateClient(string organisationJson) =>
        CreateClient(organisationJson, out _);

    private static HttpReExAccreditationClient CreateClient(
        string organisationJson, out IStructuredLogger<HttpReExAccreditationClient> logger)
    {
        var handler = new StubHttpMessageHandler(organisationJson);
        var httpClient = new HttpClient(handler);
        var config = Options.Create(new ReExAccreditationConfig { BaseUrl = "https://reex.test/" });
        logger = Substitute.For<IStructuredLogger<HttpReExAccreditationClient>>();
        return new HttpReExAccreditationClient(httpClient, config, logger);
    }

    private static string OrganisationJson(string tonnageBand) => $$"""
        {
          "registrations": [
            { "id": "reg-1", "accreditationId": "acc-1" }
          ],
          "accreditations": [
            {
              "id": "acc-1",
              "validFrom": "2025-04-01",
              "prnIssuance": {
                "tonnageBand": "{{tonnageBand}}",
                "signatories": [],
                "incomeBusinessPlan": []
              }
            }
          ]
        }
        """;

    [Theory]
    [InlineData("up_to_500", "UpTo500")]
    [InlineData("up_to_1000", "UpTo1000")]
    [InlineData("up_to_10000", "UpTo10000")]
    [InlineData("over_10000", "Over10000")]
    [InlineData("UP_TO_500", "UpTo500")]
    public async Task Maps_raw_ReEx_tonnage_band_to_canonical_value(string raw, string expected)
    {
        var client = CreateClient(OrganisationJson(raw));

        var result = await client.GetPriorYearAsync("org-1", "reg-1", 2025);

        Assert.NotNull(result);
        Assert.Equal(expected, result!.TonnageBand);
    }

    [Fact]
    public async Task Unrecognised_tonnage_band_falls_back_to_raw_value_and_logs_a_warning()
    {
        var client = CreateClient(OrganisationJson("some_future_band"), out var logger);

        var result = await client.GetPriorYearAsync("org-1", "reg-1", 2025);

        Assert.NotNull(result);
        Assert.Equal("some_future_band", result!.TonnageBand);

        logger.Received(1)
            .Log(
                LogLevel.Warning,
                Arg.Any<string>(),
                Arg.Is<IReadOnlyDictionary<string, object?>>(p =>
                    (string)p["reex.tonnage_band"]! == "some_future_band"
                ),
                null
            );
    }

    private sealed class StubHttpMessageHandler(string responseJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
