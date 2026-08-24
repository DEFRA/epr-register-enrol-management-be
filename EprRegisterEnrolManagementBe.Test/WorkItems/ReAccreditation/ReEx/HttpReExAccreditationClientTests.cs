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
        string organisationJson,
        out IStructuredLogger<HttpReExAccreditationClient> logger
    )
    {
        var handler = new StubHttpMessageHandler(organisationJson);
        var httpClient = new HttpClient(handler);
        var config = Options.Create(new ReExAccreditationConfig { BaseUrl = "https://reex.test/" });
        logger = Substitute.For<IStructuredLogger<HttpReExAccreditationClient>>();
        return new HttpReExAccreditationClient(httpClient, config, logger);
    }

    private static string OrganisationJson(string tonnageBand) =>
        $$"""
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
    [InlineData("up_to_5000", "UpTo5000")]
    [InlineData("up_to_10000", "UpTo10000")]
    [InlineData("over_10000", "Over10000")]
    [InlineData("UP_TO_500", "UpTo500")]
    public async Task Maps_raw_ReEx_tonnage_band_to_canonical_value(string raw, string expected)
    {
        var client = CreateClient(OrganisationJson(raw));

        var result = await client.GetPriorYearAsync(
            "org-1",
            "reg-1",
            2025,
            TestContext.Current.CancellationToken
        );

        Assert.NotNull(result);
        Assert.Equal(expected, result!.TonnageBand);
    }

    [Fact]
    public async Task Returns_a_result_with_empty_defaults_when_prnIssuance_is_absent()
    {
        // Covers the `prn?.` null-conditional chain's null-prn arm (tonnage
        // band, authorisers, business plan) — every other test supplies a
        // populated prnIssuance object.
        const string json = """
            {
              "registrations": [
                { "id": "reg-1", "accreditationId": "acc-1" }
              ],
              "accreditations": [
                {
                  "id": "acc-1",
                  "validFrom": "2025-04-01",
                  "prnIssuance": null
                }
              ]
            }
            """;
        var client = CreateClient(json);

        var result = await client.GetPriorYearAsync(
            "org-1",
            "reg-1",
            2025,
            TestContext.Current.CancellationToken
        );

        Assert.NotNull(result);
        Assert.Null(result!.TonnageBand);
        Assert.Empty(result.Authorisers);
        Assert.NotNull(result.BusinessPlan);
    }

    [Fact]
    public async Task Unrecognised_tonnage_band_falls_back_to_raw_value_and_logs_a_warning()
    {
        var client = CreateClient(OrganisationJson("some_future_band"), out var logger);

        var result = await client.GetPriorYearAsync(
            "org-1",
            "reg-1",
            2025,
            TestContext.Current.CancellationToken
        );

        Assert.NotNull(result);
        Assert.Equal("some_future_band", result!.TonnageBand);

        logger
            .Received(1)
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
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class StatusCodeHttpMessageHandler(HttpStatusCode statusCode)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(new HttpResponseMessage(statusCode));
    }

    private sealed class ThrowingHttpMessageHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => throw exception;
    }

    [Theory]
    [InlineData(null, "reg-1")]
    [InlineData("", "reg-1")]
    [InlineData("   ", "reg-1")]
    [InlineData("org-1", null)]
    [InlineData("org-1", "")]
    [InlineData("org-1", "   ")]
    public async Task GetPriorYearAsync_returns_null_for_missing_identifiers(
        string? organisationId,
        string? registrationId
    )
    {
        var client = CreateClient(OrganisationJson("up_to_500"));

        var result = await client.GetPriorYearAsync(
            organisationId,
            registrationId,
            2025,
            TestContext.Current.CancellationToken
        );

        Assert.Null(result);
    }

    [Fact]
    public async Task GetPriorYearAsync_returns_null_when_year_is_null()
    {
        var client = CreateClient(OrganisationJson("up_to_500"));

        var result = await client.GetPriorYearAsync(
            "org-1",
            "reg-1",
            null,
            TestContext.Current.CancellationToken
        );

        Assert.Null(result);
    }

    [Fact]
    public async Task GetPriorYearAsync_returns_null_and_logs_a_warning_on_a_non_success_status_code()
    {
        var handler = new StatusCodeHttpMessageHandler(HttpStatusCode.NotFound);
        var httpClient = new HttpClient(handler);
        var config = Options.Create(new ReExAccreditationConfig { BaseUrl = "https://reex.test/" });
        var logger = Substitute.For<IStructuredLogger<HttpReExAccreditationClient>>();
        var client = new HttpReExAccreditationClient(httpClient, config, logger);

        var result = await client.GetPriorYearAsync(
            "org-1",
            "reg-1",
            2025,
            TestContext.Current.CancellationToken
        );

        Assert.Null(result);
        logger
            .Received(1)
            .Log(
                LogLevel.Warning,
                Arg.Any<string>(),
                Arg.Is<IReadOnlyDictionary<string, object?>>(p =>
                    (string)p["reex.organisation_id"]! == "org-1"
                    && (int)p["reex.status_code"]! == (int)HttpStatusCode.NotFound
                ),
                null
            );
    }

    /// <summary>
    /// RA-475. A 401/403 from ReEx is this service's own misconfiguration, not
    /// "no prior-year record for this organisation", and it fails EVERY lookup until
    /// it is fixed. It used to log identically to a 404, which is how a total outage
    /// of the prior-year panel on dev read as ordinary noise. The degraded return is
    /// deliberately unchanged — only the severity and the message move.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task GetPriorYearAsync_logs_an_error_naming_the_credentials_when_reex_rejects_them(
        HttpStatusCode statusCode
    )
    {
        var handler = new StatusCodeHttpMessageHandler(statusCode);
        var httpClient = new HttpClient(handler);
        var config = Options.Create(new ReExAccreditationConfig { BaseUrl = "https://reex.test/" });
        var logger = Substitute.For<IStructuredLogger<HttpReExAccreditationClient>>();
        var client = new HttpReExAccreditationClient(httpClient, config, logger);

        var result = await client.GetPriorYearAsync(
            "org-1",
            "reg-1",
            2025,
            TestContext.Current.CancellationToken
        );

        Assert.Null(result);
        logger
            .Received(1)
            .Log(
                LogLevel.Error,
                Arg.Is<string>(m => m.Contains("REEX_API_BASIC_AUTH", StringComparison.Ordinal)),
                Arg.Is<IReadOnlyDictionary<string, object?>>(p =>
                    (string)p["reex.organisation_id"]! == "org-1"
                    && (int)p["reex.status_code"]! == (int)statusCode
                ),
                null
            );
        logger
            .DidNotReceive()
            .Log(
                LogLevel.Warning,
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<Exception?>()
            );
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TaskCanceledException))]
    public async Task GetPriorYearAsync_returns_null_and_logs_an_error_when_the_request_throws(
        Type exceptionType
    )
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, "boom")!;
        var handler = new ThrowingHttpMessageHandler(exception);
        var httpClient = new HttpClient(handler);
        var config = Options.Create(new ReExAccreditationConfig { BaseUrl = "https://reex.test/" });
        var logger = Substitute.For<IStructuredLogger<HttpReExAccreditationClient>>();
        var client = new HttpReExAccreditationClient(httpClient, config, logger);

        var result = await client.GetPriorYearAsync(
            "org-1",
            "reg-1",
            2025,
            TestContext.Current.CancellationToken
        );

        Assert.Null(result);
        logger
            .Received(1)
            .Log(
                LogLevel.Error,
                Arg.Any<string>(),
                Arg.Is<IReadOnlyDictionary<string, object?>>(p =>
                    (string)p["reex.organisation_id"]! == "org-1"
                ),
                exception
            );
    }

    [Fact]
    public async Task GetPriorYearAsync_returns_null_when_the_response_body_deserialises_to_null()
    {
        var client = CreateClient("null");

        var result = await client.GetPriorYearAsync(
            "org-1",
            "reg-1",
            2025,
            TestContext.Current.CancellationToken
        );

        Assert.Null(result);
    }

    [Fact]
    public async Task GetPriorYearAsync_returns_null_when_no_registration_matches()
    {
        var client = CreateClient(OrganisationJson("up_to_500").Replace("reg-1", "reg-other"));

        var result = await client.GetPriorYearAsync(
            "org-1",
            "reg-1",
            2025,
            TestContext.Current.CancellationToken
        );

        Assert.Null(result);
    }

    [Fact]
    public async Task GetPriorYearAsync_returns_null_when_no_accreditation_matches_the_registration()
    {
        const string json = """
            {
              "registrations": [
                { "id": "reg-1", "accreditationId": "acc-missing" }
              ],
              "accreditations": [
                { "id": "acc-1", "validFrom": "2025-04-01", "prnIssuance": null }
              ]
            }
            """;
        var client = CreateClient(json);

        var result = await client.GetPriorYearAsync(
            "org-1",
            "reg-1",
            2025,
            TestContext.Current.CancellationToken
        );

        Assert.Null(result);
    }

    [Fact]
    public async Task GetPriorYearAsync_returns_null_when_validFrom_does_not_parse()
    {
        const string json = """
            {
              "registrations": [
                { "id": "reg-1", "accreditationId": "acc-1" }
              ],
              "accreditations": [
                { "id": "acc-1", "validFrom": "not-a-date", "prnIssuance": null }
              ]
            }
            """;
        var client = CreateClient(json);

        var result = await client.GetPriorYearAsync(
            "org-1",
            "reg-1",
            2025,
            TestContext.Current.CancellationToken
        );

        Assert.Null(result);
    }

    [Fact]
    public async Task GetPriorYearAsync_returns_null_when_validFrom_year_does_not_match_the_requested_year()
    {
        var client = CreateClient(OrganisationJson("up_to_500"));

        // OrganisationJson's validFrom is 2025-04-01.
        var result = await client.GetPriorYearAsync(
            "org-1",
            "reg-1",
            2024,
            TestContext.Current.CancellationToken
        );

        Assert.Null(result);
    }

    [Fact]
    public async Task GetPriorYearAsync_maps_a_known_business_plan_usage_description_and_ignores_unrecognised_or_incomplete_entries()
    {
        const string json = """
            {
              "registrations": [
                { "id": "reg-1", "accreditationId": "acc-1" }
              ],
              "accreditations": [
                {
                  "id": "acc-1",
                  "validFrom": "2025-04-01",
                  "prnIssuance": {
                    "tonnageBand": "up_to_500",
                    "signatories": [
                      { "fullName": null, "email": null }
                    ],
                    "incomeBusinessPlan": [
                      { "usageDescription": "Support for business collections", "percentIncomeSpent": 15 },
                      { "usageDescription": "Activities or investment not covered by the other categories", "percentIncomeSpent": 8 },
                      { "usageDescription": "New reprocessing infrastructure and maintaining existing infrastructure", "percentIncomeSpent": 10 },
                      { "usageDescription": "Price support for buying packaging waste or selling recycled packaging waste", "percentIncomeSpent": 12 },
                      { "usageDescription": "Communications, including information campaigns", "percentIncomeSpent": 6 },
                      { "usageDescription": "Developing new markets for products made from recycled packaging waste", "percentIncomeSpent": 9 },
                      { "usageDescription": "Developing new uses for recycled packaging waste", "percentIncomeSpent": 11 },
                      { "usageDescription": "Some unrecognised category", "percentIncomeSpent": 5 },
                      { "usageDescription": null, "percentIncomeSpent": 5 },
                      { "usageDescription": "Support for business collections", "percentIncomeSpent": null }
                    ]
                  }
                }
              ]
            }
            """;
        var client = CreateClient(json, out var logger);

        var result = await client.GetPriorYearAsync(
            "org-1",
            "reg-1",
            2025,
            TestContext.Current.CancellationToken
        );

        Assert.NotNull(result);
        Assert.Equal(15, result!.BusinessPlan.BusinessCollectionsPercent);
        // RA-456: "Activities or investment not covered by the other
        // categories" is the 7th category, mapped to OtherPercent the same
        // way the other six usage descriptions map to their own setters.
        Assert.Equal(8, result.BusinessPlan.OtherPercent);
        Assert.Equal(10, result.BusinessPlan.NewInfrastructurePercent);
        Assert.Equal(12, result.BusinessPlan.PriceSupportPercent);
        Assert.Equal(6, result.BusinessPlan.CommunicationsPercent);
        Assert.Equal(9, result.BusinessPlan.NewMarketsPercent);
        Assert.Equal(11, result.BusinessPlan.NewUsesPercent);
        Assert.Equal(string.Empty, result.Authorisers[0].FullName);
        Assert.Equal(string.Empty, result.Authorisers[0].Email);

        logger
            .Received(1)
            .Log(
                LogLevel.Warning,
                Arg.Any<string>(),
                Arg.Is<IReadOnlyDictionary<string, object?>>(p =>
                    (string)p["reex.usage_description"]! == "Some unrecognised category"
                ),
                null
            );
    }

    [Fact]
    public void Constructor_does_not_set_a_base_address_when_the_configured_base_url_is_blank()
    {
        var httpClient = new HttpClient();
        var config = Options.Create(new ReExAccreditationConfig { BaseUrl = "" });
        var logger = Substitute.For<IStructuredLogger<HttpReExAccreditationClient>>();

        _ = new HttpReExAccreditationClient(httpClient, config, logger);

        Assert.Null(httpClient.BaseAddress);
    }
}
