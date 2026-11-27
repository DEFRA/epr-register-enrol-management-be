using System.Net;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.Utils.Logging;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
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
    ) => CreateClient(FakeReExHttpHandler.Json(organisationJson), out logger);

    private static HttpReExAccreditationClient CreateClient(
        HttpMessageHandler handler,
        out IStructuredLogger<HttpReExAccreditationClient> logger,
        TimeSpan? timeout = null
    )
    {
        var config = Options.Create(new ReExAccreditationConfig { BaseUrl = "https://reex.test/" });
        logger = Substitute.For<IStructuredLogger<HttpReExAccreditationClient>>();
        return new HttpReExAccreditationClient(
            new HttpClient(handler),
            config,
            logger,
            timeout ?? HttpReExAccreditationClient.RequestTimeout
        );
    }

    private static void AssertNoErrorLogged(IStructuredLogger<HttpReExAccreditationClient> logger) =>
        logger
            .DidNotReceive()
            .Log(
                LogLevel.Error,
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<Exception?>()
            );

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

        Assert.Equal(ReExReadOutcome.Found, result.Outcome);
        Assert.Equal(expected, result.Accreditation!.TonnageBand);
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

        Assert.Equal(ReExReadOutcome.Found, result.Outcome);
        Assert.Null(result.Accreditation!.TonnageBand);
        Assert.Empty(result.Accreditation!.Authorisers);
        Assert.NotNull(result.Accreditation!.BusinessPlan);
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

        Assert.Equal(ReExReadOutcome.Found, result.Outcome);
        Assert.Equal("some_future_band", result.Accreditation!.TonnageBand);

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

    [Theory]
    [InlineData(null, "reg-1")]
    [InlineData("", "reg-1")]
    [InlineData("   ", "reg-1")]
    [InlineData("org-1", null)]
    [InlineData("org-1", "")]
    [InlineData("org-1", "   ")]
    public async Task GetPriorYearAsync_throws_for_a_missing_identifier_without_calling_ReEx(
        string? organisationId,
        string? registrationId
    )
    {
        var handler = FakeReExHttpHandler.Json(OrganisationJson("up_to_500"));
        var client = CreateClient(handler, out _);

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            client.GetPriorYearAsync(
                organisationId!,
                registrationId!,
                2025,
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetPriorYearAsync_is_not_found_with_a_warning_when_reex_has_no_record_of_the_organisation()
    {
        var client = CreateClient(FakeReExHttpHandler.OrganisationNotFound(), out var logger);

        var result = await client.GetPriorYearAsync(
            "org-1",
            "reg-1",
            2025,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(PriorYearReadResult.NotFound(), result);
        logger
            .Received(1)
            .Log(
                LogLevel.Warning,
                "ReEx has no record of the organisation",
                Arg.Is<IReadOnlyDictionary<string, object?>>(p =>
                    (string)p["reex.organisation_id"]! == "org-1"
                    && (int)p["reex.status_code"]! == (int)HttpStatusCode.NotFound
                ),
                null
            );
        AssertNoErrorLogged(logger);
    }

    [Theory]
    [InlineData("""{"statusCode":404,"error":"Not Found","message":"Organisation not found"}""")]
    [InlineData("""{"statusCode":404,"error":"Not Found","message":"Organisation with id 6650f0c1a2b3c4d5e6f70809 not found"}""")]
    public async Task GetPriorYearAsync_recognises_both_of_reex_organisation_not_found_messages(string body)
    {
        var client = CreateClient(FakeReExHttpHandler.Json(body, HttpStatusCode.NotFound), out _);

        var result = await client.GetPriorYearAsync(
            "org-1",
            "reg-1",
            2025,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(ReExReadOutcome.NotFound, result.Outcome);
    }

    /// <summary>
    /// A 404 that is not ReEx's own "organisation not found" (Hapi's route
    /// miss when ReExApi:BaseUrl has the wrong path, a proxy or gateway page,
    /// no body) is ReEx being unreachable, not "no prior year".
    /// </summary>
    [Theory]
    [InlineData("""{"statusCode":404,"error":"Not Found","message":"Not Found"}""")]
    [InlineData("""{"message":"Not found"}""")]
    [InlineData("""{"message":"Registration with id reg-1 not found"}""")]
    [InlineData("<html><body>404 Not Found</body></html>")]
    [InlineData("")]
    [InlineData("[]")]
    public async Task GetPriorYearAsync_is_unavailable_with_an_error_on_a_404_that_is_not_reex_organisation_not_found(
        string body
    )
    {
        var client = CreateClient(FakeReExHttpHandler.Json(body, HttpStatusCode.NotFound), out var logger);

        var result = await client.GetPriorYearAsync(
            "org-1",
            "reg-1",
            2025,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(PriorYearReadResult.Unavailable(), result);
        logger
            .Received(1)
            .Log(
                LogLevel.Error,
                Arg.Is<string>(m => m.StartsWith("ReEx answered 404 without", StringComparison.Ordinal)),
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
    /// of the prior-year panel on dev read as ordinary noise. RA-752: it is
    /// Unavailable, never NotFound.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task GetPriorYearAsync_logs_an_error_naming_the_credentials_when_reex_rejects_them(
        HttpStatusCode statusCode
    )
    {
        var client = CreateClient(FakeReExHttpHandler.Status(statusCode), out var logger);

        var result = await client.GetPriorYearAsync(
            "org-1",
            "reg-1",
            2025,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(PriorYearReadResult.Unavailable(), result);
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

    [Fact]
    public async Task GetPriorYearAsync_is_unavailable_with_an_error_when_the_request_fails()
    {
        var exception = new HttpRequestException("boom");
        var client = CreateClient(FakeReExHttpHandler.Throwing(exception), out var logger);

        var result = await client.GetPriorYearAsync(
            "org-1",
            "reg-1",
            2025,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(PriorYearReadResult.Unavailable(), result);
        logger
            .Received(1)
            .Log(
                LogLevel.Error,
                "ReEx organisation request failed",
                Arg.Is<IReadOnlyDictionary<string, object?>>(p =>
                    (string)p["reex.organisation_id"]! == "org-1"
                ),
                exception
            );
    }

    [Fact]
    public async Task GetPriorYearAsync_is_unavailable_with_an_error_when_reex_outlasts_the_client_timeout()
    {
        // The real HttpClient timeout, as in production, shortened so the test
        // does not wait RequestTimeout.
        var client = CreateClient(FakeReExHttpHandler.Hanging(), out var logger, TimeSpan.FromMilliseconds(50));

        var result = await client.GetPriorYearAsync(
            "org-1",
            "reg-1",
            2025,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(PriorYearReadResult.Unavailable(), result);
        logger
            .Received(1)
            .Log(
                LogLevel.Error,
                "ReEx organisation request failed",
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<TaskCanceledException>()
            );
    }

    [Fact]
    public void ReEx_times_out_before_management_fe_gives_up_on_the_page()
    {
        // management-fe's BACKEND_API_TIMEOUT_MS is 5000: a hung ReEx must be
        // timed out, logged and answered 502 here before the caller has gone.
        var httpClient = new HttpClient();

        _ = new HttpReExAccreditationClient(
            httpClient,
            Options.Create(new ReExAccreditationConfig { BaseUrl = "https://reex.test/" }),
            Substitute.For<IStructuredLogger<HttpReExAccreditationClient>>()
        );

        Assert.Equal(HttpReExAccreditationClient.RequestTimeout, httpClient.Timeout);
        Assert.True(HttpReExAccreditationClient.RequestTimeout < TimeSpan.FromMilliseconds(5000));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task GetPriorYearAsync_is_unavailable_with_an_error_on_any_other_failure_status(
        HttpStatusCode statusCode
    )
    {
        var client = CreateClient(FakeReExHttpHandler.Status(statusCode), out var logger);

        var result = await client.GetPriorYearAsync(
            "org-1",
            "reg-1",
            2025,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(PriorYearReadResult.Unavailable(), result);
        logger
            .Received(1)
            .Log(
                LogLevel.Error,
                "ReEx organisation lookup failed",
                Arg.Is<IReadOnlyDictionary<string, object?>>(p =>
                    (string)p["reex.organisation_id"]! == "org-1"
                    && (int)p["reex.status_code"]! == (int)statusCode
                ),
                null
            );
    }

    [Fact]
    public async Task GetPriorYearAsync_lets_the_callers_own_cancellation_through()
    {
        // A cancelled request is the caller giving up, not ReEx failing.
        var client = CreateClient(FakeReExHttpHandler.Throwing(new TaskCanceledException("cancelled")), out var logger);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetPriorYearAsync("org-1", "reg-1", 2025, cts.Token)
        );
        AssertNoErrorLogged(logger);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("not json")]
    public async Task GetPriorYearAsync_is_unavailable_with_an_error_when_the_body_is_not_an_organisation(
        string body
    )
    {
        var client = CreateClient(body, out var logger);

        var result = await client.GetPriorYearAsync(
            "org-1",
            "reg-1",
            2025,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(PriorYearReadResult.Unavailable(), result);
        logger
            .Received(1)
            .Log(
                LogLevel.Error,
                "ReEx returned a body that is not an organisation",
                Arg.Is<IReadOnlyDictionary<string, object?>>(p =>
                    (string)p["reex.organisation_id"]! == "org-1"
                ),
                Arg.Any<Exception?>()
            );
    }

    [Fact]
    public async Task GetPriorYearAsync_is_not_found_when_no_registration_matches()
    {
        var client = CreateClient(OrganisationJson("up_to_500").Replace("reg-1", "reg-other"));

        var result = await client.GetPriorYearAsync(
            "org-1",
            "reg-1",
            2025,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(PriorYearReadResult.NotFound(), result);
    }

    [Fact]
    public async Task GetPriorYearAsync_is_not_found_when_no_accreditation_matches_the_registration()
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

        Assert.Equal(PriorYearReadResult.NotFound(), result);
    }

    [Fact]
    public async Task GetPriorYearAsync_is_not_found_when_validFrom_does_not_parse()
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

        Assert.Equal(PriorYearReadResult.NotFound(), result);
    }

    [Fact]
    public async Task GetPriorYearAsync_is_not_found_when_validFrom_year_does_not_match_the_requested_year()
    {
        var client = CreateClient(OrganisationJson("up_to_500"));

        // OrganisationJson's validFrom is 2025-04-01.
        var result = await client.GetPriorYearAsync(
            "org-1",
            "reg-1",
            2024,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(PriorYearReadResult.NotFound(), result);
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

        Assert.Equal(ReExReadOutcome.Found, result.Outcome);
        Assert.Equal(15, result.Accreditation!.BusinessPlan.BusinessCollectionsPercent);
        // RA-456: "Activities or investment not covered by the other
        // categories" is the 7th category, mapped to OtherPercent the same
        // way the other six usage descriptions map to their own setters.
        Assert.Equal(8, result.Accreditation!.BusinessPlan.OtherPercent);
        Assert.Equal(10, result.Accreditation!.BusinessPlan.NewInfrastructurePercent);
        Assert.Equal(12, result.Accreditation!.BusinessPlan.PriceSupportPercent);
        Assert.Equal(6, result.Accreditation!.BusinessPlan.CommunicationsPercent);
        Assert.Equal(9, result.Accreditation!.BusinessPlan.NewMarketsPercent);
        Assert.Equal(11, result.Accreditation!.BusinessPlan.NewUsesPercent);
        Assert.Equal(string.Empty, result.Accreditation!.Authorisers[0].FullName);
        Assert.Equal(string.Empty, result.Accreditation!.Authorisers[0].Email);

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

    // ---------------- RA-526: GetNationAsync ----------------

    private static string OrganisationJsonWithRegulator(string regulatorCode) =>
        $$"""
            {
              "registrations": [
                { "id": "reg-1", "submittedToRegulator": "{{regulatorCode}}" }
              ]
            }
            """;

    [Theory]
    [InlineData("ea", "England")]
    [InlineData("nrw", "Wales")]
    [InlineData("sepa", "Scotland")]
    [InlineData("niea", "NorthernIreland")]
    [InlineData("EA", "England")]
    public async Task GetNationAsync_maps_regulator_code_to_nation(string code, string expected)
    {
        var client = CreateClient(OrganisationJsonWithRegulator(code));

        var result = await client.GetNationAsync(
            "org-1",
            "reg-1",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(NationReadResult.Found(Enum.Parse<Nation>(expected)), result);
    }

    [Fact]
    public async Task GetNationAsync_unrecognised_regulator_code_defaults_to_England_and_logs_a_warning()
    {
        var client = CreateClient(OrganisationJsonWithRegulator("not-a-real-regulator"), out var logger);

        var result = await client.GetNationAsync(
            "org-1",
            "reg-1",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(NationReadResult.Found(Nation.England), result);
        logger
            .Received(1)
            .Log(
                LogLevel.Warning,
                Arg.Any<string>(),
                Arg.Is<IReadOnlyDictionary<string, object?>>(p =>
                    (string)p["reex.organisation_id"]! == "org-1"
                    && (string)p["reex.registration_id"]! == "reg-1"
                ),
                null
            );
    }

    [Fact]
    public async Task GetNationAsync_is_not_found_when_the_registration_is_not_in_the_organisation()
    {
        var client = CreateClient(OrganisationJsonWithRegulator("ea"));

        var result = await client.GetNationAsync(
            "org-1",
            "reg-other",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(NationReadResult.NotFound(), result);
    }

    [Fact]
    public async Task GetNationAsync_is_not_found_when_reex_has_no_record_of_the_organisation()
    {
        var client = CreateClient(FakeReExHttpHandler.OrganisationNotFound(), out _);

        var result = await client.GetNationAsync("org-1", "reg-1", TestContext.Current.CancellationToken);

        Assert.Equal(NationReadResult.NotFound(), result);
    }

    [Fact]
    public async Task GetNationAsync_is_unavailable_with_an_error_when_reex_fails()
    {
        // The RA-526 nation migration skips the item and retries it next run.
        var client = CreateClient(FakeReExHttpHandler.Status(HttpStatusCode.InternalServerError), out var logger);

        var result = await client.GetNationAsync("org-1", "reg-1", TestContext.Current.CancellationToken);

        Assert.Equal(NationReadResult.Unavailable(), result);
        logger
            .Received(1)
            .Log(
                LogLevel.Error,
                "ReEx organisation lookup failed",
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                null
            );
    }

    [Theory]
    [InlineData(null, "reg-1")]
    [InlineData(" ", "reg-1")]
    [InlineData("org-1", null)]
    [InlineData("org-1", "")]
    public async Task GetNationAsync_throws_for_a_missing_identifier_without_calling_ReEx(
        string? organisationId,
        string? registrationId
    )
    {
        var handler = FakeReExHttpHandler.Json(OrganisationJsonWithRegulator("ea"));
        var client = CreateClient(handler, out _);

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            client.GetNationAsync(organisationId!, registrationId!, TestContext.Current.CancellationToken)
        );
        Assert.Equal(0, handler.RequestCount);
    }
}
