using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using EprRegisterEnrolManagementBe.Auth;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.AddOverseasSite;

public class AddOverseasSiteSubmissionTests(MongoIntegrationFixture fixture)
{
    private const string Secret = "add-ors-test-secret";
    private EphemeralMongoTestFactory Factory() => new(fixture, "add_ors", "Development",
        new Dictionary<string, string?>
        {
            ["AUTH_SHARED_SECRET:BACKEND"] = Secret,
            ["AUTH_SHARED_SECRET:MANAGEMENT_FE"] = Secret,
            ["OperatorBackendApi:Enabled"] = "true",
            ["OperatorBackendApi:Url"] = "http://operator.test",
            ["OPERATOR_BACKEND_SHARED_SECRET"] = Secret,
        });

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, object? body = null,
        string? role = null)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        var clientId = role is null ? "epr-register-enrol-backend" : "frontend";
        request.Headers.Add("x-cdp-client-id", clientId);
        request.Headers.Add("x-cdp-user-id", "50006");
        if (role is not null) request.Headers.Add("x-cdp-user-role", role);
        request.Headers.Add("x-cdp-auth-timestamp", timestamp);
        request.Headers.Add("x-cdp-auth-nonce", nonce);
        request.Headers.Add("x-cdp-auth-signature", ClientIdAuthenticationHandler.ComputeSignature(
            Secret, new ClientIdSignaturePayload(clientId, "50006", null, timestamp, nonce, role)));
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Refusal_UsesStoredWorkflowAndRetriesTheSameRecordedDecisionAfterFailedDelivery()
    {
        using var parent = Factory();
        var callback = new OutcomeHandler();
        using var factory = parent.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddHttpClient("DefaultClient").ConfigurePrimaryHttpMessageHandler(() => callback)));
        using var client = factory.CreateClient();
        var requestId = Guid.NewGuid().ToString();
        var proposalId = Guid.NewGuid().ToString();
        using var submission = await Send(client, HttpMethod.Post, "/work-items", new
        {
            typeId = "add-overseas-site", payload = new
            {
                operatorRequestId = requestId, schemaVersion = 1, proposedSiteId = proposalId,
                target = new { organisationId = "50006", registrationId = "registration", accreditationId = "granted", materialType = "Glass", year = 2026 },
                proposal = new { siteName = "Spike Glass Recycling", siteAddress = "25 Example Road, Hamburg", country = "Germany" },
                baselineSites = Array.Empty<object>(),
            },
        });
        Assert.Equal(HttpStatusCode.Created, submission.StatusCode);
        var receipt = await submission.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var id = receipt.GetProperty("id").GetString();
        var module = $"/work-items/add-overseas-site/{id}";
        Assert.Equal(HttpStatusCode.Conflict, (await Send(client, HttpMethod.Post, module + "/refuse", role: "standard")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, HttpMethod.Post, module + "/start-assessment", role: "support-readonly")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, HttpMethod.Post, module + "/start-assessment")).StatusCode);
        var started = await Send(client, HttpMethod.Post, module + "/start-assessment", role: "standard");
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var assessed = await Read(client, "/work-items/" + id);
        Assert.Equal("assessment-in-progress", assessed.GetProperty("stateId").GetString());
        Assert.Equal("v1", assessed.GetProperty("templateVersion").GetString());
        Assert.Contains(assessed.GetProperty("auditLog").EnumerateArray(), entry =>
            entry.GetProperty("details").TryGetProperty("actionId", out var action) && action.GetString() == "start-assessment");
        foreach (var action in new[] { "approve", "reject", "withdraw" })
            Assert.NotEqual(HttpStatusCode.OK, (await Send(client, HttpMethod.Post, $"/work-items/{id}/actions/{action}", role: "standard")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Send(client, HttpMethod.Post, module + "/refuse", role: "standard")).StatusCode);
        var failed = await Read(client, "/work-items/" + id);
        Assert.Equal("rejected", failed.GetProperty("stateId").GetString());
        Assert.False(failed.GetProperty("payload").TryGetProperty("outcomeDeliveryConfirmed", out var delivered) && delivered.GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await Send(client, HttpMethod.Post, module + "/start-assessment", role: "standard")).StatusCode);
        callback.Accept = true;
        Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Post, module + "/refuse", role: "standard")).StatusCode);
        var confirmed = await Read(client, "/work-items/" + id);
        Assert.True(confirmed.GetProperty("payload").GetProperty("outcomeDeliveryConfirmed").GetBoolean());
        Assert.Equal(failed.GetProperty("auditLog").GetArrayLength(), confirmed.GetProperty("auditLog").GetArrayLength());
        Assert.Equal(callback.Bodies[0], callback.Bodies[1]);
        using var outcome = JsonDocument.Parse(callback.Bodies[0]);
        Assert.Equal(requestId, outcome.RootElement.GetProperty("requestId").GetString());
        Assert.Equal(id, outcome.RootElement.GetProperty("caseId").GetString());
        Assert.Equal(proposalId, outcome.RootElement.GetProperty("proposedSiteId").GetString());
        Assert.Equal("rejected", outcome.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Post, module + "/refuse", role: "standard")).StatusCode);
        Assert.Equal(2, callback.Bodies.Count);
    }

    private sealed class OutcomeHandler : HttpMessageHandler
    {
        public bool Accept { get; set; }
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/api/v1/accreditation-changes/outcome", request.RequestUri!.AbsolutePath);
            Assert.True(request.Headers.Contains("x-cdp-auth-signature"));
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(Accept ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable);
        }
    }

    private static async Task<JsonElement> Read(HttpClient client, string path)
    {
        using var response = await Send(client, HttpMethod.Get, path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Submission_ReplaysOneCaseAndListsAndReadsItsStoredTemplateAndCorrelation()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var requestId = Guid.NewGuid().ToString();
        var siteId = Guid.NewGuid().ToString();
        var body = new
        {
            typeId = "add-overseas-site",
            payload = new
            {
                operatorRequestId = requestId, schemaVersion = 1, proposedSiteId = siteId,
                target = new { organisationId = "50006", registrationId = "registration", accreditationId = "granted", materialType = "Glass", year = 2026 },
                proposal = new { siteName = "Spike Glass Recycling", siteAddress = "25 Example Road, Hamburg", country = "Germany" },
                baselineSites = Array.Empty<object>(), organisationName = "Global Glass Exports",
            },
        };
        var submissions = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Send(client, HttpMethod.Post, "/work-items", body)));
        foreach (var submission in submissions) Assert.Equal(HttpStatusCode.Created, submission.StatusCode);
        var receipts = await Task.WhenAll(submissions.Select(r => r.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)));
        var id = receipts[0].GetProperty("id").GetString();
        Assert.All(receipts, receipt => Assert.Equal(id, receipt.GetProperty("id").GetString()));
        var detail = await Read(client, "/work-items/" + id);
        Assert.Equal("submitted", detail.GetProperty("stateId").GetString());
        Assert.Equal("v1", detail.GetProperty("templateVersion").GetString());
        Assert.Equal(requestId, detail.GetProperty("payload").GetProperty("operatorRequestId").GetString());
        Assert.Equal(siteId, detail.GetProperty("payload").GetProperty("proposedSiteId").GetString());
        var changedTarget = JsonSerializer.SerializeToNode(body)!;
        changedTarget["payload"]!["target"]!["accreditationId"] = "unrelated-accreditation";
        using var conflictingReplay = await Send(client, HttpMethod.Post, "/work-items", changedTarget);
        Assert.Equal(HttpStatusCode.BadRequest, conflictingReplay.StatusCode);
        var unchanged = await Read(client, "/work-items/" + id);
        Assert.Equal("granted", unchanged.GetProperty("payload").GetProperty("target").GetProperty("accreditationId").GetString());
        Assert.Empty(detail.GetProperty("availableActions").EnumerateArray());
        var list = await Read(client, "/work-items?typeIds=add-overseas-site");
        Assert.Equal(1, list.GetProperty("totalCount").GetInt32());
        Assert.All(list.GetProperty("items").EnumerateArray(), item => Assert.Equal("add-overseas-site", item.GetProperty("typeId").GetString()));
        var forbidden = await Send(client, HttpMethod.Post, "/work-items/" + id + "/actions/start-assessment");
        Assert.NotEqual(HttpStatusCode.OK, forbidden.StatusCode);
    }
    [Fact]
    public async Task Submission_RejectsUnsignedCallerAndIncompleteChangeRequest()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-cdp-client-id", "epr-register-enrol-backend");
        var unsigned = await client.PostAsJsonAsync("/work-items", new { typeId = "add-overseas-site" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        client.DefaultRequestHeaders.Clear();
        var malformed = await Send(client, HttpMethod.Post, "/work-items", new { typeId = "add-overseas-site", payload = new { operatorRequestId = "invalid" } });
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        var list = await Read(client, "/work-items?typeIds=add-overseas-site");
        Assert.Equal(0, list.GetProperty("totalCount").GetInt32());
    }

}
