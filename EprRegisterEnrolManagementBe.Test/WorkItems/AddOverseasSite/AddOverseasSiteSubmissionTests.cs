using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using EprRegisterEnrolManagementBe.Auth;
using EprRegisterEnrolManagementBe.Test.TestSupport;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.AddOverseasSite;

public class AddOverseasSiteSubmissionTests(MongoIntegrationFixture fixture)
{
    private const string Secret = "add-ors-test-secret";
    private EphemeralMongoTestFactory Factory() => new(fixture, "add_ors", "Development",
        new Dictionary<string, string?> { ["AUTH_SHARED_SECRET:BACKEND"] = Secret });

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, object? body = null)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("x-cdp-client-id", "epr-register-enrol-backend");
        request.Headers.Add("x-cdp-user-id", "50006");
        request.Headers.Add("x-cdp-auth-timestamp", timestamp);
        request.Headers.Add("x-cdp-auth-nonce", nonce);
        request.Headers.Add("x-cdp-auth-signature", ClientIdAuthenticationHandler.ComputeSignature(
            Secret, new ClientIdSignaturePayload("epr-register-enrol-backend", "50006", null, timestamp, nonce)));
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
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
