using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using static EprRegisterEnrolManagementBe.Test.TestSupport.AddOverseasSiteAppFactory;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.AddOverseasSite;

/// <summary>
/// RA-770: <c>GET /work-items/{id}/related-work-items?page=</c>, the Related
/// requests tab of an add-ORS case. Driven through the real ASP.NET pipeline
/// against ephemeral MongoDB, from submissions posted to <c>POST /work-items</c>
/// as the operator backend posts them.
/// </summary>
public sealed class AddOverseasSiteRelatedWorkItemsEndpointTests(MongoIntegrationFixture fixture)
{
    [Fact]
    public async Task Lists_the_submission_with_the_one_asked_about_first_then_the_rest_in_reference_order()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var ids = await factory.SubmitAsync(client, "related-tab-1", siteCount: 3, ct);
        await factory.SubmitAsync(client, "related-tab-other-submission", siteCount: 2, ct);
        Assert.Equal(HttpStatusCode.OK, (await PostDulyMakeAsync(client, ids[2], ct)).StatusCode);

        var body = await GetRelatedAsync(client, ids[1], page: null, ct);

        Assert.Equal(ids[1], body.GetProperty("workItemId").GetGuid());
        Assert.Equal(3, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(10, body.GetProperty("pageSize").GetInt32());
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(
            [
                (ids[1], "IYC-ORS-2026-00002", "Overseas site 2", "ORS-002", "submitted"),
                (ids[0], "IYC-ORS-2026-00001", "Overseas site 1", "ORS-001", "submitted"),
                (ids[2], "IYC-ORS-2026-00003", "Overseas site 3", "ORS-003", "duly-made"),
            ],
            items.Select(i =>
                (
                    i.GetProperty("id").GetGuid(),
                    i.GetProperty("applicationReference").GetString(),
                    i.GetProperty("siteName").GetString(),
                    i.GetProperty("orsId").GetString(),
                    i.GetProperty("stateId").GetString()
                )
            )
        );
        Assert.All(items, i => Assert.Equal("add-overseas-site", i.GetProperty("typeId").GetString()));
        Assert.All(items, i => Assert.Equal(SiteChargePence, i.GetProperty("chargePence").GetInt32()));
    }

    [Fact]
    public async Task Pages_a_large_submission_ten_at_a_time_keeping_the_one_asked_about_first_on_page_one()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var ids = await factory.SubmitAsync(client, "related-tab-large", siteCount: 12, ct);

        var page1 = await GetRelatedAsync(client, ids[4], page: 1, ct);
        var page2 = await GetRelatedAsync(client, ids[4], page: 2, ct);

        Assert.Equal(12, page1.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            [ids[4], ids[0], ids[1], ids[2], ids[3], ids[5], ids[6], ids[7], ids[8], ids[9]],
            ItemIds(page1)
        );
        Assert.Equal(2, page2.GetProperty("page").GetInt32());
        Assert.Equal([ids[10], ids[11]], ItemIds(page2));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(99, 2)]
    public async Task A_page_out_of_range_is_the_nearest_page(int asked, int expected)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var ids = await factory.SubmitAsync(client, "related-tab-range", siteCount: 11, ct);

        var body = await GetRelatedAsync(client, ids[0], page: asked, ct);

        Assert.Equal(expected, body.GetProperty("page").GetInt32());
        Assert.NotEmpty(ItemIds(body));
    }

    [Fact]
    public async Task A_request_submitted_on_its_own_lists_only_itself()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var ids = await factory.SubmitAsync(client, "related-tab-single", siteCount: 1, ct);

        var body = await GetRelatedAsync(client, ids[0], page: null, ct);

        Assert.Equal(1, body.GetProperty("totalCount").GetInt32());
        Assert.Equal([ids[0]], ItemIds(body));
    }

    [Fact]
    public async Task The_work_item_read_counts_the_work_items_in_its_submission_for_the_tab_label()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var three = await factory.SubmitAsync(client, "related-count-three", siteCount: 3, ct);
        var single = await factory.SubmitAsync(client, "related-count-single", siteCount: 1, ct);

        var ofThree = await client.GetFromJsonAsync<JsonElement>($"/work-items/{three[2]}", ct);
        var ofSingle = await client.GetFromJsonAsync<JsonElement>($"/work-items/{single[0]}", ct);

        Assert.Equal(3, ofThree.GetProperty("submissionWorkItemCount").GetInt32());
        Assert.Equal(1, ofSingle.GetProperty("submissionWorkItemCount").GetInt32());
    }

    [Fact]
    public async Task An_unknown_work_item_is_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/work-items/{Guid.NewGuid()}/related-work-items", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<JsonElement> GetRelatedAsync(HttpClient client, Guid id, int? page, CancellationToken ct)
    {
        var query = page is null ? string.Empty : $"?page={page}";
        var response = await client.GetAsync($"/work-items/{id}/related-work-items{query}", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private static List<Guid> ItemIds(JsonElement body) =>
        body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
}
