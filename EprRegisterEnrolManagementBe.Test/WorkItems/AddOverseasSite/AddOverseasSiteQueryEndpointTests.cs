using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using static EprRegisterEnrolManagementBe.Test.TestSupport.OverseasSiteAppFactory;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.AddOverseasSite;

/// <summary>
/// RA-698 (MBE-1 to MBE-5): the regulator queries an add-ORS item, the
/// operator backend answers it, and the caseworker continues the review.
/// Driven through the real ASP.NET pipeline with the production modules
/// registered and a fake push adapter, against ephemeral MongoDB.
/// </summary>
public sealed class AddOverseasSiteQueryEndpointTests(MongoIntegrationFixture fixture)
{
    private const string Reason = "Please confirm the site address and upload the BES certificate.";

    /// <summary>A one-site submission whose site carries address, contact and one interim site.</summary>
    private static async Task<Guid> SubmitWithInterimSiteAsync(
        OverseasSiteAppFactory factory,
        HttpClient client,
        string operatorRequestId,
        CancellationToken ct
    )
    {
        var body = AddOverseasSiteSubmissions.Body(operatorRequestId, siteCount: 1, siteChargePence: SiteChargePence);
        var site = body["payload"]!["sites"]![0]!.AsObject();
        site["addressLine1"] = "1 Old Street";
        site["postcode"] = "75001";
        site["contactName"] = "Old Contact";
        site["repatriatedLoads"] = "Old arrangements";
        site["interimSites"] = new JsonArray(
            new JsonObject
            {
                ["siteId"] = 7,
                ["siteNumber"] = "I-001",
                ["country"] = "Belgium",
                ["siteName"] = "Old interim",
                ["addressLine1"] = "9 Interim Road",
            }
        );
        var response = await client.PostAsJsonAsync("/work-items", body, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return created.GetProperty("workItems")[0].GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> PostQueryAsync(HttpClient client, Guid id, object body, CancellationToken ct) =>
        client.PostAsJsonAsync($"/work-items/{id}/query", body, ct);

    private static object ResumeBody(
        object? site = null,
        object? interimSites = null,
        object? besEvidenceFiles = null
    ) =>
        new
        {
            responderContactDetails = new { fullName = "Ada Operator", email = "ada@example.test", role = "Director" },
            submittedAt = DateTime.UtcNow,
            site = site ?? new { siteName = "New site name" },
            interimSites = interimSites ?? Array.Empty<object>(),
            besEvidenceFiles = besEvidenceFiles ?? Array.Empty<object>(),
        };

    private static Task<HttpResponseMessage> PostResumeAsync(
        HttpClient client,
        Guid id,
        object body,
        CancellationToken ct
    ) => client.PostAsJsonAsync($"/work-items/add-overseas-site/{id}/resume-from-query", body, ct);

    private async Task<(OverseasSiteAppFactory Factory, HttpClient Client, Guid Id)> QueriedAsync(
        string operatorRequestId,
        CancellationToken ct,
        OverseasSiteAppFactory? factory = null
    )
    {
        factory ??= new OverseasSiteAppFactory(fixture);
        var client = factory.CreateClient();
        var id = await SubmitWithInterimSiteAsync(factory, client, operatorRequestId, ct);
        Assert.Equal(HttpStatusCode.OK, (await PostQueryAsync(client, id, new { reason = Reason }, ct)).StatusCode);
        return (factory, client, id);
    }

    [Fact]
    public async Task A_reason_only_query_moves_a_submitted_item_to_queried_and_pushes_the_note()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, client, id) = await QueriedAsync("query-submitted", ct);
        await using var _ = factory;
        using var __ = client;

        var item = await factory.WaitForAuditAsync(id, "query-push-sent", ct);
        Assert.Equal("queried", item.StateId);
        Assert.Equal("alice-1", item.AssignedToId);

        var queried = Assert.Single(item.AuditLog, a => a.Action == "application-queried");
        Assert.Equal("query-during-duly-making", queried.Details["actionId"]);
        Assert.Equal(Reason, queried.Details["reason"]);
        Assert.False(queried.Details.ContainsKey("sections"));

        Assert.Equal([(id, Reason)], factory.QueryPushes);
        // The query keeps its own push: no status push is sent for it.
        Assert.Empty(factory.Pushes);
        Assert.Equal(Reason, item.Payload["currentQuery"]["reason"].AsString);
    }

    [Fact]
    public async Task A_started_item_is_queried_through_the_assessment_transition()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await factory.DulyMadeAndStartedAsync(client, "query-started", ct);

        Assert.Equal(HttpStatusCode.OK, (await PostQueryAsync(client, id, new { reason = Reason }, ct)).StatusCode);

        var item = await factory.WaitForAuditAsync(id, "application-queried", ct);
        Assert.Equal("queried", item.StateId);
        Assert.Equal(
            "query-during-assessment",
            Assert.Single(item.AuditLog, a => a.Action == "application-queried").Details["actionId"]
        );
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"reason\":\"   \"}")]
    [InlineData("{\"reason\":\"Why\",\"sections\":[\"overseas-reprocessing-sites\"]}")]
    public async Task An_invalid_query_body_is_a_400_and_changes_nothing(string json)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = (await factory.SubmitAsync(client, "query-invalid", siteCount: 1, ct))[0];

        var response = await client.PostAsync(
            $"/work-items/{id}/query",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            ct
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("submitted", (await factory.Persistence.GetByIdAsync(id, ct))!.StateId);
        Assert.Empty(factory.QueryPushes);
    }

    [Fact]
    public async Task Querying_an_unknown_item_is_a_404()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();

        var response = await PostQueryAsync(client, Guid.NewGuid(), new { reason = Reason }, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_item_cannot_be_queried_twice()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, client, id) = await QueriedAsync("query-twice", ct);
        await using var _ = factory;
        using var __ = client;

        var response = await PostQueryAsync(client, id, new { reason = "Again" }, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_decided_item_cannot_be_queried()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await factory.DulyMadeAndStartedAsync(client, "query-decided", ct);
        Assert.Equal(HttpStatusCode.OK, (await PostDecisionAsync(client, id, "rejected", ct)).StatusCode);

        var response = await PostQueryAsync(client, id, new { reason = Reason }, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task The_generic_action_route_cannot_apply_a_query_resume_or_continue_review_transition()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = (await factory.SubmitAsync(client, "query-generic", siteCount: 1, ct))[0];

        var response = await client.PostAsync($"/work-items/{id}/actions/query-during-duly-making", content: null, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid action", (await response.Content.ReadFromJsonAsync<ProblemDetails>(ct))!.Title);
        Assert.Equal("submitted", (await factory.Persistence.GetByIdAsync(id, ct))!.StateId);
    }

    [Fact]
    public async Task A_disabled_query_push_is_recorded_as_skipped_and_the_query_still_succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new OverseasSiteAppFactory(fixture);
        factory
            .PushAdapter.PushChangeRequestQueryRaisedAsync(
                Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(OperatorBackendPushResult.Skipped("OperatorBackendApi:Enabled is false.")));
        var (_, client, id) = await QueriedAsync("query-skipped", ct, factory);
        await using var _f = factory;
        using var _c = client;

        var item = await factory.WaitForAuditAsync(id, "query-push-skipped", ct);

        Assert.Equal("queried", item.StateId);
        Assert.DoesNotContain(item.AuditLog, a => a.Action is "query-push-failed" or "query-push-sent");
    }

    [Fact]
    public async Task A_failed_query_push_is_recorded_and_the_query_still_succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new OverseasSiteAppFactory(fixture);
        factory
            .PushAdapter.PushChangeRequestQueryRaisedAsync(
                Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(OperatorBackendPushResult.Failure("Operator backend returned 500.")));
        var (_, client, id) = await QueriedAsync("query-failed", ct, factory);
        await using var _f = factory;
        using var _c = client;

        var item = await factory.WaitForAuditAsync(id, "query-push-failed", ct);

        Assert.Equal("queried", item.StateId);
        Assert.Equal(
            "Operator backend returned 500.",
            Assert.Single(item.AuditLog, a => a.Action == "query-push-failed").Details["errorMessage"]
        );
    }

    [Fact]
    public async Task A_response_updates_the_site_and_interim_sites_records_what_changed_and_is_not_echoed_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, client, id) = await QueriedAsync("resume-ok", ct);
        await using var _ = factory;
        using var __ = client;

        var response = await PostResumeAsync(
            client,
            id,
            ResumeBody(
                site: new
                {
                    siteName = "New site name",
                    addressLine1 = "2 New Street",
                    rejectedLoadsArrangements = "New arrangements",
                },
                interimSites: new[]
                {
                    new
                    {
                        siteId = 7,
                        siteNumber = "I-001",
                        changes = new { siteName = "New interim", postcode = "AB1 2CD" },
                    },
                },
                besEvidenceFiles: new[]
                {
                    new
                    {
                        fileId = "file-1",
                        filename = "bes.pdf",
                        contentType = "application/pdf",
                        s3Key = "bes/file-1.pdf",
                        s3Bucket = "bucket",
                        besEvidenceValidFromDate = "2026-01-01",
                        besEvidenceExpiryDate = "2027-01-01",
                    },
                }
            ),
            ct
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = (await factory.Persistence.GetByIdAsync(id, ct))!;
        Assert.Equal("updated", item.StateId);
        // Re-assigned to the querying caseworker.
        Assert.Equal("alice-1", item.AssignedToId);

        // payload.site and its interim site carry the current values; locked ones are untouched.
        var site = item.Payload["site"].AsBsonDocument;
        Assert.Equal("New site name", site["siteName"].AsString);
        Assert.Equal("2 New Street", site["addressLine1"].AsString);
        Assert.Equal("New arrangements", site["repatriatedLoads"].AsString);
        Assert.False(site.Contains("rejectedLoadsArrangements"));
        Assert.Equal("Old Contact", site["contactName"].AsString);
        Assert.Equal("France", site["country"].AsString);
        var interim = site["interimSites"].AsBsonArray.Single().AsBsonDocument;
        Assert.Equal("New interim", interim["siteName"].AsString);
        Assert.Equal("AB1 2CD", interim["postcode"].AsString);
        Assert.Equal("9 Interim Road", interim["addressLine1"].AsString);
        Assert.Equal("Belgium", interim["country"].AsString);

        Assert.Equal("file-1", item.Payload["site"]["besEvidence"]["files"][0]["fileId"].AsString);
        // The latest response is on the payload for caseworkers.
        var latest = item.Payload["latestQueryResponse"].AsBsonDocument;
        Assert.Equal("file-1", latest["besEvidenceFiles"].AsBsonArray.Single()["fileId"].AsString);
        Assert.Equal("Overseas site 1", latest["siteChanges"]["siteName"]["from"].AsString);

        // The audit log records who responded, what changed (old and new) and the new files.
        var responded = Assert.Single(item.AuditLog, a => a.Action == "query-responded");
        Assert.Equal("resume-during-duly-making", responded.Details["actionId"]);
        Assert.Equal("Ada Operator", responded.Details["responderFullName"]);
        Assert.Equal("ada@example.test", responded.Details["responderEmail"]);
        Assert.Equal("Director", responded.Details["responderRole"]);
        Assert.Equal("bes-evidence:file-1:bes.pdf", responded.Details["fileReferences"]);
        using var siteChanges = JsonDocument.Parse(responded.Details["siteChanges"]!);
        Assert.Equal("Overseas site 1", siteChanges.RootElement.GetProperty("siteName").GetProperty("from").GetString());
        Assert.Equal("New site name", siteChanges.RootElement.GetProperty("siteName").GetProperty("to").GetString());
        using var interimChanges = JsonDocument.Parse(responded.Details["interimSiteChanges"]!);
        var interimChange = interimChanges.RootElement[0];
        Assert.Equal("I-001", interimChange.GetProperty("siteNumber").GetString());
        Assert.Equal("Old interim", interimChange.GetProperty("changes").GetProperty("siteName").GetProperty("from").GetString());

        // No status-push echo of the move onto Updated (give a background push time to arrive).
        await Task.Delay(500, ct);
        Assert.Empty(factory.Pushes);
        Assert.DoesNotContain((await factory.Persistence.GetByIdAsync(id, ct))!.AuditLog, a => a.Action.StartsWith("status-push"));
    }

    [Fact]
    public async Task A_repeated_response_is_an_idempotent_replay()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, client, id) = await QueriedAsync("resume-twice", ct);
        await using var _ = factory;
        using var __ = client;
        Assert.Equal(HttpStatusCode.OK, (await PostResumeAsync(client, id, ResumeBody(), ct)).StatusCode);

        var again = await PostResumeAsync(client, id, ResumeBody(site: new { siteName = "Other" }), ct);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var item = (await factory.Persistence.GetByIdAsync(id, ct))!;
        Assert.Single(item.AuditLog, a => a.Action == "query-responded");
        Assert.Equal("New site name", item.Payload["site"]["siteName"].AsString);
    }

    [Fact]
    public async Task A_response_to_an_item_that_was_not_queried_is_a_409()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = (await factory.SubmitAsync(client, "resume-not-queried", siteCount: 1, ct))[0];

        var response = await PostResumeAsync(client, id, ResumeBody(), ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("submitted", (await factory.Persistence.GetByIdAsync(id, ct))!.StateId);
    }

    [Theory]
    [InlineData("country")]
    [InlineData("orsId")]
    public async Task A_response_that_changes_a_locked_field_is_a_400_and_changes_nothing(string field)
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, client, id) = await QueriedAsync($"resume-locked-{field}", ct);
        await using var _ = factory;
        using var __ = client;

        var response = await PostResumeAsync(
            client,
            id,
            ResumeBody(site: new Dictionary<string, string> { [field] = "Elsewhere" }),
            ct
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var item = (await factory.Persistence.GetByIdAsync(id, ct))!;
        Assert.Equal("queried", item.StateId);
        Assert.Equal("France", item.Payload["site"]["country"].AsString);
    }

    [Fact]
    public async Task An_interim_site_change_matching_no_interim_site_is_recorded_but_not_applied()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, client, id) = await QueriedAsync("resume-unmatched", ct);
        await using var _ = factory;
        using var __ = client;

        var response = await PostResumeAsync(
            client,
            id,
            ResumeBody(interimSites: new[] { new { siteId = 99, siteNumber = "I-099", changes = new { siteName = "Ghost" } } }),
            ct
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = (await factory.Persistence.GetByIdAsync(id, ct))!;
        Assert.Equal("Old interim", item.Payload["site"]["interimSites"][0]["siteName"].AsString);
        Assert.Equal("I-099", Assert.Single(item.AuditLog, a => a.Action == "query-responded").Details["unmatchedInterimSites"]);
    }

    [Fact]
    public async Task An_updated_item_reports_the_state_it_was_queried_from_as_its_origin()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, client, id) = await QueriedAsync("origin-state", ct);
        await using var _ = factory;
        using var __ = client;
        Assert.Equal(HttpStatusCode.OK, (await PostResumeAsync(client, id, ResumeBody(), ct)).StatusCode);

        var item = await client.GetFromJsonAsync<JsonElement>($"/work-items/{id}", ct);

        Assert.Equal("updated", item.GetProperty("stateId").GetString());
        Assert.Equal("submitted", item.GetProperty("originStateId").GetString());
    }

    [Fact]
    public async Task Continue_review_moves_an_updated_item_back_to_where_it_was_queried_from_and_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, client, id) = await QueriedAsync("continue-review", ct);
        await using var _ = factory;
        using var __ = client;
        Assert.Equal(HttpStatusCode.OK, (await PostResumeAsync(client, id, ResumeBody(), ct)).StatusCode);

        var response = await client.PostAsync($"/work-items/{id}/continue-review", content: null, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("submitted", (await factory.Persistence.GetByIdAsync(id, ct))!.StateId);

        var replay = await client.PostAsync($"/work-items/{id}/continue-review", content: null, ct);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(replay.Headers.Contains(WorkItemEndpoints.IdempotentReplayHeader));
    }

    [Fact]
    public async Task Continue_review_on_a_queried_item_is_a_409()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, client, id) = await QueriedAsync("continue-queried", ct);
        await using var _ = factory;
        using var __ = client;

        var response = await client.PostAsync($"/work-items/{id}/continue-review", content: null, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
