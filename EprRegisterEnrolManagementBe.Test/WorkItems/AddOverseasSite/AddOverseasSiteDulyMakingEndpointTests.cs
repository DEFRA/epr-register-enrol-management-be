using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using static EprRegisterEnrolManagementBe.Test.TestSupport.AddOverseasSiteAppFactory;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.AddOverseasSite;

/// <summary>
/// RA-495 (AC4): an add-ORS work item is duly made on its own through the
/// shared Standard-lifecycle routes (<c>GET</c>/<c>POST /work-items/{id}/duly-make</c>).
/// Driven through the real ASP.NET pipeline with the production modules
/// registered, against ephemeral MongoDB, from a submission posted to
/// <c>POST /work-items</c> as the operator backend posts it. The
/// operator-backend push adapter is a fake (pushes: AddOverseasSiteStatusPushEndpointTests).
/// </summary>
public sealed class AddOverseasSiteDulyMakingEndpointTests(MongoIntegrationFixture fixture)
{
    [Fact]
    public async Task Duly_making_one_site_moves_only_that_work_item_to_duly_made_on_the_payment_date_clock()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var ids = await factory.SubmitAsync(client, "duly-make-1", siteCount: 2, ct);

        var response = await PostDulyMakeAsync(client, ids[0], PaymentDate.ToString("yyyy-MM-dd"), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("duly-made", body.GetProperty("stateId").GetString());

        var dulyMade = await factory.Persistence.GetByIdAsync(ids[0], ct);
        Assert.Equal("duly-made", dulyMade!.StateId);
        Assert.Equal(PaymentDate.ToString("yyyy-MM-dd"), dulyMade.Payload["paymentDate"].AsString);
        Assert.Equal(PaymentDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), dulyMade.SlaClock!.StartedAt);
        Assert.Equal(TimeSpan.FromDays(84), dulyMade.SlaClock.TargetDuration);
        var applied = Assert.Single(dulyMade.AuditLog, a => a.Action == "action-applied");
        Assert.Equal("duly-make", applied.Details["actionId"]);
        Assert.Equal("submitted", applied.Details["fromStateId"]);
        Assert.Equal("duly-made", applied.Details["toStateId"]);
        Assert.Contains(dulyMade.AuditLog, a => a.Action == "sla-clock-started");

        var sibling = await factory.Persistence.GetByIdAsync(ids[1], ct);
        Assert.Equal("submitted", sibling!.StateId);
        Assert.Null(sibling.SlaClock);
    }

    [Theory]
    [InlineData(null, "payment-date-required")]
    [InlineData("03/04/2026", "payment-date-invalid")]
    [InlineData("2999-01-01", "payment-date-in-future")]
    public async Task Duly_making_validates_the_payment_date_as_re_accreditation_does(
        string? paymentDate,
        string errorCode
    )
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var ids = await factory.SubmitAsync(client, "duly-make-invalid", siteCount: 1, ct);

        var response = await PostDulyMakeAsync(client, ids[0], paymentDate, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(errorCode, problem.GetProperty("errorCode").GetString());
        Assert.Equal("paymentDate", problem.GetProperty("field").GetString());
        var persisted = await factory.Persistence.GetByIdAsync(ids[0], ct);
        Assert.Equal("submitted", persisted!.StateId);
        Assert.DoesNotContain(persisted.AuditLog, a => a.Action == "action-applied");
    }

    [Fact]
    public async Task A_work_item_already_duly_made_cannot_be_duly_made_again()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var ids = await factory.SubmitAsync(client, "duly-make-twice", siteCount: 1, ct);
        var first = await PostDulyMakeAsync(client, ids[0], PaymentDate.ToString("yyyy-MM-dd"), ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await PostDulyMakeAsync(client, ids[0], PaymentDate.AddDays(-1).ToString("yyyy-MM-dd"), ct);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(ids[0], ct);
        Assert.Equal(PaymentDate.ToString("yyyy-MM-dd"), persisted!.Payload["paymentDate"].AsString);
        Assert.Single(persisted.AuditLog, a => a.Action == "action-applied");
    }

    [Fact]
    public async Task Duly_make_details_carry_the_sites_own_charge()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var ids = await factory.SubmitAsync(client, "duly-make-charge", siteCount: 1, ct);

        var details = await client.GetFromJsonAsync<JsonElement>($"/work-items/{ids[0]}/duly-make", ct);

        Assert.Equal(SiteChargePence, details.GetProperty("chargePence").GetInt32());
    }

    [Fact]
    public async Task Duly_make_details_list_the_other_work_items_from_the_same_submission_with_their_current_status()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var ids = await factory.SubmitAsync(client, "related-1", siteCount: 3, ct);
        await factory.SubmitAsync(client, "related-other-submission", siteCount: 2, ct);
        var dulyMade = await PostDulyMakeAsync(client, ids[1], PaymentDate.ToString("yyyy-MM-dd"), ct);
        Assert.Equal(HttpStatusCode.OK, dulyMade.StatusCode);

        var details = await client.GetFromJsonAsync<JsonElement>($"/work-items/{ids[0]}/duly-make", ct);

        var related = details.GetProperty("relatedWorkItems").EnumerateArray().ToList();
        Assert.Equal(
            [
                (ids[1], "IYC-ORS-2026-00002", "Overseas site 2", "ORS-002", "duly-made"),
                (ids[2], "IYC-ORS-2026-00003", "Overseas site 3", "ORS-003", "submitted"),
            ],
            related.Select(r =>
                (
                    r.GetProperty("id").GetGuid(),
                    r.GetProperty("applicationReference").GetString(),
                    r.GetProperty("siteName").GetString(),
                    r.GetProperty("orsId").GetString(),
                    r.GetProperty("stateId").GetString()
                )
            )
        );
        Assert.All(related, r => Assert.Equal("add-overseas-site", r.GetProperty("typeId").GetString()));
        Assert.All(related, r => Assert.Equal(SiteChargePence, r.GetProperty("chargePence").GetInt32()));
    }

    [Fact]
    public async Task Duly_make_details_list_no_related_work_items_for_a_single_site_submission()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var ids = await factory.SubmitAsync(client, "related-single", siteCount: 1, ct);

        var details = await client.GetFromJsonAsync<JsonElement>($"/work-items/{ids[0]}/duly-make", ct);

        Assert.Empty(details.GetProperty("relatedWorkItems").EnumerateArray());
    }
}
