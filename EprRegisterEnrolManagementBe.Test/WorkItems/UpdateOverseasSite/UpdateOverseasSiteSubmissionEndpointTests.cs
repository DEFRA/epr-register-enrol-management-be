using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.UpdateOverseasSite;
using static EprRegisterEnrolManagementBe.Test.TestSupport.OverseasSiteAppFactory;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.UpdateOverseasSite;

/// <summary>
/// RA-682: an ORS update submission posted to <c>POST /work-items</c>, through
/// the real ASP.NET pipeline with the production modules registered, against
/// ephemeral MongoDB. RA-767: the operator backend sends a request holding both
/// adds and updates as one POST, each site naming its own type.
/// </summary>
public sealed class UpdateOverseasSiteSubmissionEndpointTests(MongoIntegrationFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> PostCreatedAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/work-items", body, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static List<string?> ApplicationReferences(JsonElement body) =>
        body.GetProperty("workItems")
            .EnumerateArray()
            .Select(w => w.GetProperty("applicationReference").GetString())
            .ToList();

    [Fact]
    public async Task Two_site_updates_without_a_payment_reference_become_two_submitted_work_items()
    {
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();

        var body = await PostCreatedAsync(
            client,
            UpdateOverseasSiteSubmissions.Body("request-1")
        );

        Assert.Equal(["IYC-ORS-2026-00001", "IYC-ORS-2026-00002"], ApplicationReferences(body));
        foreach (var (entry, index) in body.GetProperty("workItems").EnumerateArray().Select((w, i) => (w, i)))
        {
            var stored = await factory.Persistence.GetByIdAsync(entry.GetProperty("id").GetGuid(), Ct);
            Assert.NotNull(stored);
            Assert.Equal(UpdateOverseasSiteType.Id, stored!.TypeId);
            Assert.Equal("submitted", stored.StateId);
            // No operator payment reference was sent, so Core's RA-503 fallback stamps the work
            // item's own reference, as for any submission without one.
            Assert.Equal(
                entry.GetProperty("applicationReference").GetString(),
                stored.Payload["paymentReference"].AsString
            );
            var site = stored.Payload["site"].AsBsonDocument;
            Assert.Equal($"ORS-{index + 1:D3}", site["orsId"].AsString);
            Assert.Equal($"Renamed site {index + 1}", site["proposed"]["siteName"].AsString);
            Assert.Equal($"Overseas site {index + 1}", site["previous"]["siteName"].AsString);
            var interim = site["interimSiteChanges"].AsBsonArray;
            Assert.Equal(["remove", "add"], interim.Select(c => c["action"].AsString));
        }
    }

    // RA-682: dropping the payment reference is the update type's alone.
    [Fact]
    public async Task An_add_ORS_submission_without_a_payment_reference_is_still_refused()
    {
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var add = AddOverseasSiteSubmissions.Body("request-no-payment");
        add["payload"]!.AsObject().Remove("paymentReference");

        var response = await client.PostAsJsonAsync("/work-items", add, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await factory.CountAsync("workItems", Ct));
    }

    // RA-767 AC2: one operator request holding an add and two updates is one
    // POST. Each site is created as its own type, all under one submission.
    [Fact]
    public async Task One_post_with_an_add_and_two_updates_creates_three_work_items_under_one_submission()
    {
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();

        var body = await PostCreatedAsync(client, MixedRequest("request-mixed"));

        Assert.Equal("SUB-2026-0001", body.GetProperty("submissionReference").GetString());
        // add-ORS and the ORS update share the IYC-ORS series (RA-682).
        Assert.Equal(["IYC-ORS-2026-00001", "IYC-ORS-2026-00002", "IYC-ORS-2026-00003"], ApplicationReferences(body));
        Assert.Equal(
            [AddOverseasSiteSubmissions.TypeId, UpdateOverseasSiteType.Id, UpdateOverseasSiteType.Id],
            body.GetProperty("workItems").EnumerateArray().Select(w => w.GetProperty("typeId").GetString())
        );
        var stored = new List<WorkItem>();
        foreach (var id in WorkItemIds(body))
        {
            stored.Add((await factory.Persistence.GetByIdAsync(id, Ct))!);
        }
        Assert.Equal(
            [AddOverseasSiteSubmissions.TypeId, UpdateOverseasSiteType.Id, UpdateOverseasSiteType.Id],
            stored.Select(w => w.TypeId)
        );
        Assert.Equal(["v2", "v1", "v1"], stored.Select(w => w.TemplateVersion));
        Assert.All(stored, w => Assert.Equal("submitted", w.StateId));
        Assert.All(stored, w => Assert.Equal("SUB-2026-0001", w.Payload["submissionReference"].AsString));
        Assert.All(stored, w => Assert.Equal(3, w.Payload["submissionSize"].AsInt32));
        Assert.All(stored, w => Assert.False(w.Payload["site"].AsBsonDocument.Contains("typeId")));
        // The operator paid for the add; an update falls back to its own reference.
        Assert.Equal("PR/PK/REP/500500", stored[0].Payload["paymentReference"].AsString);
        Assert.Equal("IYC-ORS-2026-00002", stored[1].Payload["paymentReference"].AsString);
        Assert.Equal(1, await factory.CountAsync("submissions", Ct));

        var related = await client.GetFromJsonAsync<JsonElement>($"/work-items/{stored[1].Id}/related-work-items", Ct);
        Assert.Equal(3, related.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task A_replayed_mixed_request_returns_all_three_work_items()
    {
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var request = MixedRequest("request-replay");
        var first = await client.PostAsJsonAsync("/work-items", request, Ct);
        var firstBody = await first.Content.ReadAsStringAsync(Ct);

        var replay = await client.PostAsJsonAsync("/work-items", request, Ct);

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal("true", Assert.Single(replay.Headers.GetValues("X-Idempotent-Replay")));
        Assert.Equal(firstBody, await replay.Content.ReadAsStringAsync(Ct));
        var replayed = await replay.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(3, replayed.GetProperty("workItems").GetArrayLength());
        Assert.Equal(3, await factory.CountAsync("workItems", Ct));
        Assert.Equal(1, await factory.CountAsync("submissions", Ct));
    }

    // The replay guard is still keyed on the request's typeId.
    [Fact]
    public async Task A_request_id_reused_under_another_request_type_is_refused()
    {
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        await PostCreatedAsync(client, MixedRequest("request-reused"));
        var other = MixedRequest("request-reused");
        other["typeId"] = UpdateOverseasSiteType.Id;

        var response = await client.PostAsJsonAsync("/work-items", other, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(3, await factory.CountAsync("workItems", Ct));
    }

    // RA-767: an update request naming an add site needs the add's payment reference.
    [Fact]
    public async Task An_update_request_holding_an_add_without_a_payment_reference_is_refused_and_creates_nothing()
    {
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var request = UpdateOverseasSiteSubmissions.Body("request-unpaid", siteCount: 1);
        var addSite = AddOverseasSiteSubmissions.Body("unused", siteCount: 1)["payload"]!["sites"]![0]!.DeepClone();
        addSite["typeId"] = AddOverseasSiteSubmissions.TypeId;
        Sites(request).Add(addSite);

        var response = await client.PostAsJsonAsync("/work-items", request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Contains("paymentReference", problem.GetProperty("detail").GetString());
        await AssertNothingCreatedAsync(factory);

        Payload(request)["paymentReference"] = "PR/PK/REP/500500";
        var paid = await PostCreatedAsync(client, request);
        Assert.Equal("SUB-2026-0001", paid.GetProperty("submissionReference").GetString());
    }

    public static TheoryData<string, string?> FailingSites =>
        new()
        {
            { "an unregistered type", "unknown-site-type" },
            { "a type that is not split per site", "unknown-site-type" },
            { "a type id that is not a string", "unknown-site-type" },
            { "a site missing its orsId", null },
        };

    // RA-767 AC2: one site that cannot be accepted fails the whole request.
    [Theory]
    [MemberData(nameof(FailingSites))]
    public async Task One_failing_site_refuses_the_whole_request_and_creates_nothing(string failure, string? errorCode)
    {
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var request = MixedRequest("request-failing");
        var lastSite = Sites(request)[2]!.AsObject();
        switch (failure)
        {
            case "an unregistered type": lastSite["typeId"] = "no-such-type"; break;
            case "a type that is not split per site": lastSite["typeId"] = ReAccreditationType.Id; break;
            case "a type id that is not a string": lastSite["typeId"] = 5; break;
            case "a site missing its orsId": lastSite.Remove("orsId"); break;
            default: throw new ArgumentOutOfRangeException(nameof(failure), failure, null);
        }

        var response = await client.PostAsJsonAsync("/work-items", request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(errorCode, problem.TryGetProperty("errorCode", out var code) ? code.GetString() : null);
        await AssertNothingCreatedAsync(factory);

        // Nothing was allocated either: the next request takes the first numbers.
        var next = await PostCreatedAsync(client, MixedRequest("request-next"));
        Assert.Equal("SUB-2026-0001", next.GetProperty("submissionReference").GetString());
        Assert.Equal("IYC-ORS-2026-00001", ApplicationReferences(next)[0]);
    }

    /// <summary>An add-ORS request with one add site, then two update sites naming their own type.</summary>
    private static JsonObject MixedRequest(string operatorRequestId)
    {
        var request = AddOverseasSiteSubmissions.Body(operatorRequestId, siteCount: 1);
        var updates = UpdateOverseasSiteSubmissions.Body("unused", siteCount: 2);
        foreach (var site in Sites(updates))
        {
            var update = site!.DeepClone();
            update["typeId"] = UpdateOverseasSiteType.Id;
            Sites(request).Add(update);
        }
        return request;
    }

    private static async Task AssertNothingCreatedAsync(OverseasSiteAppFactory factory)
    {
        Assert.Equal(0, await factory.CountAsync("workItems", Ct));
        Assert.Equal(0, await factory.CountAsync("submissions", Ct));
    }

    private static JsonObject Payload(JsonObject body) => body["payload"]!.AsObject();

    private static JsonArray Sites(JsonObject body) => Payload(body)["sites"]!.AsArray();

    private static List<Guid> WorkItemIds(JsonElement body) =>
        body.GetProperty("workItems").EnumerateArray().Select(w => w.GetProperty("id").GetGuid()).ToList();
}
