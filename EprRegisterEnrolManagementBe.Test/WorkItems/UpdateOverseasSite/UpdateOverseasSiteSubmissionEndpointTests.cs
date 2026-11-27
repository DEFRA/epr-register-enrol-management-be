using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using EprRegisterEnrolManagementBe.WorkItems.UpdateOverseasSite;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using static EprRegisterEnrolManagementBe.Test.TestSupport.OverseasSiteAppFactory;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.UpdateOverseasSite;

/// <summary>
/// RA-682: an ORS update submission posted to <c>POST /work-items</c>, through
/// the real ASP.NET pipeline with the production modules registered, against
/// ephemeral MongoDB. The operator backend sends a request holding both adds
/// and updates as one submission per change type: the add group under the
/// request's id and the update group under <c>{requestId}:update-overseas-site</c>.
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
            UpdateOverseasSiteSubmissions.Body($"request-1:{UpdateOverseasSiteType.Id}")
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

    // RA-682: one operator request holding two adds and one update arrives as
    // two submissions. Both are created, from one IYC-ORS series, and each
    // replays its own work items.
    [Fact]
    public async Task An_add_group_and_an_update_group_from_one_request_both_create_their_work_items()
    {
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var add = AddOverseasSiteSubmissions.Body("request-mixed", siteCount: 2);
        var update = UpdateOverseasSiteSubmissions.Body($"request-mixed:{UpdateOverseasSiteType.Id}", siteCount: 1);

        var addBody = await PostCreatedAsync(client, add);
        var updateBody = await PostCreatedAsync(client, update);

        Assert.Equal(["IYC-ORS-2026-00001", "IYC-ORS-2026-00002"], ApplicationReferences(addBody));
        Assert.Equal(["IYC-ORS-2026-00003"], ApplicationReferences(updateBody));
        Assert.Equal(3, await factory.CountAsync("workItems", Ct));
        Assert.Equal(2, await factory.CountAsync("submissions", Ct));

        var addReplay = await PostCreatedAsync(client, add);
        var updateReplay = await PostCreatedAsync(client, update);
        Assert.Equal(ApplicationReferences(addBody), ApplicationReferences(addReplay));
        Assert.Equal(ApplicationReferences(updateBody), ApplicationReferences(updateReplay));
        Assert.Equal(3, await factory.CountAsync("workItems", Ct));
    }

    // RA-767 AC1: the two groups of one operator request are one submission.
    // The later group sends the reference the first was given and both send
    // the request's total size, so every work item carries the same
    // reference and size and the related-work-items tab of either lists both.
    [Fact]
    public async Task A_later_group_sent_with_the_first_groups_reference_shares_its_reference_and_total_size()
    {
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var add = AddOverseasSiteSubmissions.Body("request-shared", siteCount: 1);
        Payload(add)["submissionSize"] = 2;

        var addBody = await PostCreatedAsync(client, add);
        var reference = addBody.GetProperty("submissionReference").GetString();
        var updateBody = await PostCreatedAsync(client, LaterGroup("request-shared", reference, submissionSize: 2));

        Assert.Equal("SUB-2026-0001", reference);
        Assert.Equal(reference, updateBody.GetProperty("submissionReference").GetString());
        var ids = WorkItemIds(addBody).Concat(WorkItemIds(updateBody)).ToList();
        foreach (var id in ids)
        {
            var stored = await factory.Persistence.GetByIdAsync(id, Ct);
            Assert.Equal(reference, stored!.Payload["submissionReference"].AsString);
            Assert.Equal(2, stored.Payload["submissionSize"].AsInt32);

            var related = await client.GetFromJsonAsync<JsonElement>($"/work-items/{id}/related-work-items", Ct);
            Assert.Equal(2, related.GetProperty("totalCount").GetInt32());
            Assert.Equal(
                ids.Order(),
                related.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Order()
            );
        }
        Assert.Equal(2, await factory.CountAsync("submissions", Ct));

        // The shared reference used no number: the next submission gets 0002.
        var next = await PostCreatedAsync(client, AddOverseasSiteSubmissions.Body("request-next", siteCount: 1));
        Assert.Equal("SUB-2026-0002", next.GetProperty("submissionReference").GetString());
    }

    [Fact]
    public async Task A_replayed_later_group_returns_its_stored_submission()
    {
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var add = AddOverseasSiteSubmissions.Body("request-replay", siteCount: 1);
        Payload(add)["submissionSize"] = 2;
        var addBody = await PostCreatedAsync(client, add);
        var update = LaterGroup("request-replay", addBody.GetProperty("submissionReference").GetString(), submissionSize: 2);
        var first = await client.PostAsJsonAsync("/work-items", update, Ct);
        var firstBody = await first.Content.ReadAsStringAsync(Ct);

        var replay = await client.PostAsJsonAsync("/work-items", update, Ct);

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal("true", Assert.Single(replay.Headers.GetValues("X-Idempotent-Replay")));
        Assert.Equal(firstBody, await replay.Content.ReadAsStringAsync(Ct));
        Assert.Equal(2, await factory.CountAsync("workItems", Ct));
        Assert.Equal(2, await factory.CountAsync("submissions", Ct));
    }

    public static TheoryData<string> RefusedReferences =>
        new("another request's reference", "another organisation", "no submission for the base request", "a made-up reference");

    [Theory]
    [MemberData(nameof(RefusedReferences))]
    public async Task A_later_group_whose_reference_is_not_its_requests_is_refused_and_creates_nothing(string refusal)
    {
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var mine = await PostCreatedAsync(client, AddOverseasSiteSubmissions.Body("request-mine", siteCount: 1));
        var theirs = await PostCreatedAsync(client, AddOverseasSiteSubmissions.Body("request-theirs", siteCount: 1));
        var myReference = mine.GetProperty("submissionReference").GetString();
        var update = refusal switch
        {
            "another request's reference" => LaterGroup("request-mine", theirs.GetProperty("submissionReference").GetString()),
            "another organisation" => LaterGroup("request-mine", myReference, organisationId: "org-2"),
            "no submission for the base request" => LaterGroup("request-unknown", myReference),
            "a made-up reference" => LaterGroup("request-mine", "SUB-2026-9999"),
            _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal, null),
        };

        var response = await client.PostAsJsonAsync("/work-items", update, Ct);

        await AssertRefusedAsync(response, "submission-reference-not-found");
        Assert.Equal(2, await factory.CountAsync("workItems", Ct));
        Assert.Equal(2, await factory.CountAsync("submissions", Ct));
    }

    [Fact]
    public async Task A_reference_sent_on_a_request_that_is_not_a_later_group_is_refused()
    {
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var first = await PostCreatedAsync(client, AddOverseasSiteSubmissions.Body("request-first", siteCount: 1));
        var add = AddOverseasSiteSubmissions.Body("request-second", siteCount: 1);
        Payload(add)["submissionReference"] = first.GetProperty("submissionReference").GetString();

        var response = await client.PostAsJsonAsync("/work-items", add, Ct);

        await AssertRefusedAsync(response, "submission-reference-not-allowed");
        Assert.Equal(1, await factory.CountAsync("workItems", Ct));
        Assert.Equal(1, await factory.CountAsync("submissions", Ct));
    }

    [Theory]
    [InlineData("smaller than the sites sent", "invalid-submission-size")]
    [InlineData("not a number", "invalid-submission-size")]
    [InlineData("blank reference", "invalid-submission-reference")]
    public async Task A_malformed_shared_field_is_refused_and_creates_nothing(string malformation, string errorCode)
    {
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var update = UpdateOverseasSiteSubmissions.Body($"request-bad:{UpdateOverseasSiteType.Id}", siteCount: 2);
        switch (malformation)
        {
            case "smaller than the sites sent": Payload(update)["submissionSize"] = 1; break;
            case "not a number": Payload(update)["submissionSize"] = "3"; break;
            case "blank reference": Payload(update)["submissionReference"] = " "; break;
            default: throw new ArgumentOutOfRangeException(nameof(malformation), malformation, null);
        }

        var response = await client.PostAsJsonAsync("/work-items", update, Ct);

        await AssertRefusedAsync(response, errorCode);
        Assert.Equal(0, await factory.CountAsync("workItems", Ct));
    }

    // Unchanged without the shared fields: each group is its own submission.
    [Fact]
    public async Task Groups_sent_without_the_shared_fields_get_their_own_reference_and_size()
    {
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var add = AddOverseasSiteSubmissions.Body("request-separate", siteCount: 2);
        Payload(add)["submissionReference"] = null;
        Payload(add)["submissionSize"] = null;

        var addBody = await PostCreatedAsync(client, add);
        var updateBody = await PostCreatedAsync(
            client,
            UpdateOverseasSiteSubmissions.Body($"request-separate:{UpdateOverseasSiteType.Id}", siteCount: 1)
        );

        Assert.Equal("SUB-2026-0001", addBody.GetProperty("submissionReference").GetString());
        Assert.Equal("SUB-2026-0002", updateBody.GetProperty("submissionReference").GetString());
        var update = await factory.Persistence.GetByIdAsync(WorkItemIds(updateBody)[0], Ct);
        Assert.Equal(1, update!.Payload["submissionSize"].AsInt32);
        var added = await factory.Persistence.GetByIdAsync(WorkItemIds(addBody)[0], Ct);
        Assert.Equal(2, added!.Payload["submissionSize"].AsInt32);
    }

    // RA-767 review: the issuing group need not be stored under the bare base id.
    [Fact]
    public async Task A_later_group_shares_a_reference_issued_to_an_earlier_group_with_a_group_suffix()
    {
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var add = AddOverseasSiteSubmissions.Body("request-suffixed:first", siteCount: 1);
        Payload(add)["submissionSize"] = 2;
        var reference = (await PostCreatedAsync(client, add)).GetProperty("submissionReference").GetString();

        var updateBody = await PostCreatedAsync(client, LaterGroup("request-suffixed", reference, submissionSize: 2));

        Assert.Equal(reference, updateBody.GetProperty("submissionReference").GetString());
    }

    // A record stored before RA-767 holds no organisation or size, so the work
    // items carrying the reference vouch for the organisation and the size.
    [Theory]
    [InlineData("org-1", true)]
    [InlineData("org-2", false)]
    public async Task A_later_group_of_a_request_stored_before_RA_767_is_checked_against_its_work_items(
        string organisationId,
        bool accepted
    )
    {
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var add = AddOverseasSiteSubmissions.Body("request-legacy", siteCount: 1);
        Payload(add)["submissionSize"] = 3;
        var reference = (await PostCreatedAsync(client, add)).GetProperty("submissionReference").GetString();
        await Submissions(factory)
            .UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", "request-legacy"),
                Builders<BsonDocument>.Update.Unset("operatorOrganisationId").Unset("submissionSize"),
                cancellationToken: Ct
            );

        var response = await client.PostAsJsonAsync(
            "/work-items",
            LaterGroup("request-legacy", reference, submissionSize: null, organisationId: organisationId),
            Ct
        );

        if (!accepted)
        {
            await AssertRefusedAsync(response, "submission-reference-not-found");
            Assert.Equal(1, await factory.CountAsync("workItems", Ct));
            return;
        }
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(reference, body.GetProperty("submissionReference").GetString());
        var update = await factory.Persistence.GetByIdAsync(WorkItemIds(body)[0], Ct);
        Assert.Equal(3, update!.Payload["submissionSize"].AsInt32);
    }

    [Fact]
    public async Task A_later_group_that_omits_the_size_is_stamped_with_the_stored_one()
    {
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var add = AddOverseasSiteSubmissions.Body("request-no-size", siteCount: 1);
        Payload(add)["submissionSize"] = 4;
        var reference = (await PostCreatedAsync(client, add)).GetProperty("submissionReference").GetString();

        var body = await PostCreatedAsync(client, LaterGroup("request-no-size", reference, submissionSize: null));

        var update = await factory.Persistence.GetByIdAsync(WorkItemIds(body)[0], Ct);
        Assert.Equal(4, update!.Payload["submissionSize"].AsInt32);
        var stored = await Submissions(factory)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", $"request-no-size:{UpdateOverseasSiteType.Id}"))
            .SingleAsync(Ct);
        Assert.Equal(4, stored["submissionSize"].AsInt32);
    }

    [Theory]
    [InlineData("a different size", 1, 3)]
    [InlineData("a stored size smaller than its sites", 2, null)]
    public async Task A_later_group_whose_size_does_not_fit_the_stored_one_is_refused(
        string _,
        int siteCount,
        int? submissionSize
    )
    {
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var reference = (await PostCreatedAsync(client, AddOverseasSiteSubmissions.Body("request-size", siteCount: 1)))
            .GetProperty("submissionReference")
            .GetString();

        var response = await client.PostAsJsonAsync(
            "/work-items",
            LaterGroup("request-size", reference, submissionSize, siteCount: siteCount),
            Ct
        );

        await AssertRefusedAsync(response, "invalid-submission-size");
        Assert.Equal(1, await factory.CountAsync("workItems", Ct));
        Assert.Equal(1, await factory.CountAsync("submissions", Ct));
    }

    [Fact]
    public async Task A_size_over_the_upper_bound_is_refused()
    {
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var add = AddOverseasSiteSubmissions.Body("request-huge", siteCount: 1);
        Payload(add)["submissionSize"] = 1001;

        var response = await client.PostAsJsonAsync("/work-items", add, Ct);

        await AssertRefusedAsync(response, "invalid-submission-size");
        Assert.Equal(0, await factory.CountAsync("workItems", Ct));
    }

    private static IMongoCollection<BsonDocument> Submissions(OverseasSiteAppFactory factory) =>
        factory.Services.GetRequiredService<IMongoDbClientFactory>().GetCollection<BsonDocument>("submissions");

    private static JsonObject Payload(JsonObject body) => body["payload"]!.AsObject();

    private static JsonObject LaterGroup(
        string requestId,
        string? submissionReference,
        int? submissionSize = 2,
        string organisationId = "org-1",
        int siteCount = 1
    )
    {
        var body = UpdateOverseasSiteSubmissions.Body($"{requestId}:{UpdateOverseasSiteType.Id}", siteCount: siteCount);
        Payload(body)["submissionReference"] = submissionReference;
        Payload(body)["submissionSize"] = submissionSize;
        Payload(body)["operatorOrganisationId"] = organisationId;
        return body;
    }

    private static List<Guid> WorkItemIds(JsonElement body) =>
        body.GetProperty("workItems").EnumerateArray().Select(w => w.GetProperty("id").GetGuid()).ToList();

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string errorCode)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(errorCode, problem.GetProperty("errorCode").GetString());
    }
}
