using System.Net;
using System.Text;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Utils.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Polly;
using Polly.Retry;

namespace EprRegisterEnrolManagementBe.Test.Integrations.OperatorBackend;

/// <summary>
/// RA-769: the operator backend read adapter maps every answer onto a typed outcome and never throws. A fast retry
/// pipeline stands in for the production one, as in the sibling adapters' tests.
/// </summary>
public class HttpAccreditationSummaryAdapterTests
{
    private const string BaseUrl = "https://operator-backend.example.test";

    private const string UsableBody =
        """
        {
          "organisationName": "Acme Recycling Ltd",
          "organisationNumber": 500500,
          "accreditationNumber": "A26ER5003240164GR",
          "registrationNumber": "R26ER5003240164GR",
          "accreditationYear": 2026,
          "wasteProcessingType": "exporter",
          "material": "glass",
          "glassRecyclingProcess": "glass_re_melt",
          "siteAddress": "1 High Street",
          "companyRegisteredAddress": "2 Registered Road"
        }
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (HttpAccreditationSummaryAdapter Adapter, Handler Handler) BuildSut(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        bool enabled = true,
        string url = BaseUrl,
        string? sharedSecret = "a-shared-secret"
    )
    {
        var handler = new Handler(respond);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("DefaultClient").Returns(_ => new HttpClient(handler, disposeHandler: false));
        var adapter = new HttpAccreditationSummaryAdapter(
            factory,
            Options.Create(
                new OperatorBackendApiConfig
                {
                    Enabled = enabled,
                    Url = url,
                    ClientId = "epr-register-enrol-management-be",
                    SharedSecret = sharedSecret,
                }
            ),
            NullLogger<HttpAccreditationSummaryAdapter>.Instance,
            Substitute.For<IStructuredLogger<HttpAccreditationSummaryAdapter>>(),
            new ResiliencePipelineBuilder<HttpResponseMessage>()
                .AddRetry(
                    new RetryStrategyOptions<HttpResponseMessage>
                    {
                        MaxRetryAttempts = 2,
                        Delay = TimeSpan.Zero,
                        ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                            .Handle<HttpRequestException>()
                            .HandleResult(r => (int)r.StatusCode >= 500),
                    }
                )
                .Build()
        );
        return (adapter, handler);
    }

    private static Task<AccreditationSummaryReadResult> Read(HttpAccreditationSummaryAdapter adapter) =>
        adapter.GetAccreditationSummaryAsync(
            new AccreditationSummaryReadRequest("org 1", "app-1", Guid.NewGuid()),
            Ct
        );

    [Fact]
    public async Task A_usable_summary_is_a_success_read_with_a_signed_get_to_the_case_management_route()
    {
        var (adapter, handler) = BuildSut(_ => Json(HttpStatusCode.OK, UsableBody));

        var result = await Read(adapter);

        Assert.Equal(AccreditationSummaryReadOutcome.Success, result.Outcome);
        Assert.Equal("A26ER5003240164GR", result.Summary!.AccreditationNumber);
        Assert.Equal("glass_re_melt", result.Summary.GlassRecyclingProcess);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            $"{BaseUrl}/api/v1/accreditation-applications/case-management/org%201/app-1/accreditation-summary",
            request.RequestUri!.AbsoluteUri
        );
        Assert.True(request.Headers.Contains("x-cdp-auth-signature"));
        Assert.Equal("epr-register-enrol-management-be", request.Headers.GetValues("x-cdp-client-id").Single());
    }

    [Fact]
    public async Task A_404_is_not_found_and_is_not_retried()
    {
        var (adapter, handler) = BuildSut(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await Read(adapter);

        Assert.Equal(AccreditationSummaryReadOutcome.NotFound, result.Outcome);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_5xx_is_retried_and_then_a_transient_failure()
    {
        var (adapter, handler) = BuildSut(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var result = await Read(adapter);

        Assert.Equal(AccreditationSummaryReadOutcome.TransientFailure, result.Outcome);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task An_unexpected_4xx_is_a_transient_failure_and_is_not_retried()
    {
        var (adapter, handler) = BuildSut(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await Read(adapter);

        Assert.Equal(AccreditationSummaryReadOutcome.TransientFailure, result.Outcome);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_transport_failure_is_a_transient_failure_and_never_throws()
    {
        var (adapter, _) = BuildSut(_ => throw new HttpRequestException("connection refused"));

        var result = await Read(adapter);

        Assert.Equal(AccreditationSummaryReadOutcome.TransientFailure, result.Outcome);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{ "material": "glass" }""")]
    [InlineData("""{ "accreditationYear": 2026 }""")]
    [InlineData("""{ "accreditationYear": "twenty", "material": "glass" }""")]
    public async Task An_unusable_body_is_a_transient_failure(string body)
    {
        var (adapter, _) = BuildSut(_ => Json(HttpStatusCode.OK, body));

        var result = await Read(adapter);

        Assert.Equal(AccreditationSummaryReadOutcome.TransientFailure, result.Outcome);
        Assert.Null(result.Summary);
    }

    [Fact]
    public async Task A_disabled_integration_makes_no_call_and_is_a_transient_failure()
    {
        var (adapter, handler) = BuildSut(_ => Json(HttpStatusCode.OK, UsableBody), enabled: false);

        var result = await Read(adapter);

        Assert.Equal(AccreditationSummaryReadOutcome.TransientFailure, result.Outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task An_unconfigured_url_makes_no_call_and_is_a_transient_failure()
    {
        var (adapter, handler) = BuildSut(_ => Json(HttpStatusCode.OK, UsableBody), url: "");

        var result = await Read(adapter);

        Assert.Equal(AccreditationSummaryReadOutcome.TransientFailure, result.Outcome);
        Assert.Empty(handler.Requests);
    }
}
