using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.UpdateOverseasSite;
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

    // RA-682 AC07: an approved update goes back to the operator backend with its
    // effective date, on the same change-request route as add-ORS.
    [Fact]
    public async Task An_approved_update_is_pushed_back_with_its_effective_date()
    {
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var body = await PostCreatedAsync(
            client,
            UpdateOverseasSiteSubmissions.Body($"request-approve:{UpdateOverseasSiteType.Id}", siteCount: 1)
        );
        var id = body.GetProperty("workItems")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await PostDulyMakeAsync(client, id, Ct)).StatusCode);
        await factory.WaitForAuditAsync(id, "status-push-sent", Ct);
        Assert.Equal(HttpStatusCode.OK, (await PostStartAssessmentAsync(client, id, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await PostDecisionAsync(client, id, "approved", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostConfirmContactAsync(client, id, Ct)).StatusCode);

        var closed = await factory.WaitForAuditAsync(id, "status-push-sent", Ct, count: 2);
        Assert.Equal("approved", closed.StateId);
        var decision = Assert.Single(factory.Pushes, p => p.Pipeline == OperatorBackendPushPipeline.Decision);
        Assert.Equal(OperatorBackendStatusRoute.ChangeRequest, decision.Route);
        Assert.Equal(EffectiveDate, decision.EffectiveDate);
    }
}
