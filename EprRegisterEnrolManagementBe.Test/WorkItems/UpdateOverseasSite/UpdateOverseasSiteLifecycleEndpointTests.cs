using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.UpdateOverseasSite;
using static EprRegisterEnrolManagementBe.Test.TestSupport.OverseasSiteAppFactory;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.UpdateOverseasSite;

/// <summary>
/// An ORS update through the shared Standard-lifecycle routes. It is never
/// charged and the operator backend applies it when told of the approval, so it
/// takes neither a payment date at duly making nor a valid-from date at the
/// decision. Driven through the real ASP.NET pipeline with the production
/// modules registered, against ephemeral MongoDB.
/// </summary>
public sealed class UpdateOverseasSiteLifecycleEndpointTests(MongoIntegrationFixture fixture)
{
    private const OperatorBackendStatusRoute ChangeRequest = OperatorBackendStatusRoute.ChangeRequest;
    private const OperatorBackendPushPipeline BestEffort = OperatorBackendPushPipeline.BestEffort;
    private const OperatorBackendPushPipeline Decision = OperatorBackendPushPipeline.Decision;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Duly_make_details_say_no_payment_date_is_taken()
    {
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await SubmitAsync(client, "details");

        var details = await client.GetFromJsonAsync<JsonElement>($"/work-items/{id}/duly-make", Ct);

        Assert.False(details.GetProperty("takesPaymentDate").GetBoolean());
        Assert.Equal(0, details.GetProperty("chargePence").GetInt32());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Duly_make_without_a_payment_date_starts_the_sla_clock_when_it_is_duly_made(string? paymentDate)
    {
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await SubmitAsync(client, $"duly-make-{paymentDate is null}");

        var before = DateTime.UtcNow;
        var response = await PostDulyMakeAsync(client, id, paymentDate, Ct);
        var after = DateTime.UtcNow;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dulyMade = await factory.Persistence.GetByIdAsync(id, Ct);
        Assert.Equal("duly-made", dulyMade!.StateId);
        Assert.False(dulyMade.Payload.Contains("paymentDate"));
        Assert.InRange(dulyMade.SlaClock!.StartedAt, before.AddSeconds(-1), after.AddSeconds(1));
        // The same 12 weeks as re-accreditation, from the duly make.
        Assert.Equal(TimeSpan.FromDays(84), dulyMade.SlaClock.TargetDuration);
        Assert.Equal(dulyMade.SlaClock.StartedAt.AddDays(84), dulyMade.SlaClock.DueAt);

        var applied = Assert.Single(dulyMade.AuditLog, a => a.Action == "action-applied");
        Assert.Equal("duly-make", applied.Details["actionId"]);
        Assert.False(applied.Details.ContainsKey("paymentDate"));
        var clock = Assert.Single(dulyMade.AuditLog, a => a.Action == "sla-clock-started");
        Assert.Equal("duly-made", clock.Details["anchoredTo"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task Duly_make_refuses_a_payment_date_with_nothing_changed(int daysFromToday)
    {
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await SubmitAsync(client, $"payment-date-{-daysFromToday}");
        var paymentDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(daysFromToday);

        var response = await PostDulyMakeAsync(client, id, paymentDate.ToString("yyyy-MM-dd"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("payment-date-not-taken", problem.GetProperty("errorCode").GetString());
        Assert.Equal("paymentDate", problem.GetProperty("field").GetString());
        var untouched = await factory.Persistence.GetByIdAsync(id, Ct);
        Assert.Equal("submitted", untouched!.StateId);
        Assert.Null(untouched.SlaClock);
        Assert.False(untouched.Payload.Contains("paymentDate"));
        Assert.DoesNotContain(untouched.AuditLog, a => a.Action != "work-item-submitted");
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    public async Task The_decision_takes_no_valid_from_date_and_the_push_carries_none(string outcome)
    {
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await DulyMadeAndStartedAsync(factory, client, $"decide-{outcome}");

        // The approval sends no effectiveDate: the type does not ask for one.
        var decision = await PostDecisionAsync(client, id, outcome, effectiveDate: null, Ct);

        Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
        var pending = await factory.Persistence.GetByIdAsync(id, Ct);
        Assert.Equal($"{outcome}-pending-contact", pending!.StateId);
        Assert.False(pending.Payload.Contains("effectiveDate"));
        var determination = Assert.Single(pending.AuditLog, a => a.Action == "determination-made");
        Assert.False(determination.Details.ContainsKey("effectiveDate"));

        Assert.Equal(HttpStatusCode.OK, (await PostConfirmContactAsync(client, id, Ct)).StatusCode);

        var closed = await factory.WaitForAuditAsync(id, "status-push-sent", Ct, count: 2);
        Assert.Equal(outcome, closed.StateId);
        Assert.False(closed.Payload.Contains("effectiveDate"));
        Assert.Equal(
            [
                new Push(ChangeRequest, BestEffort, id, "submitted", "duly-made", "duly-make"),
                new Push(ChangeRequest, Decision, id, $"{outcome}-pending-contact", outcome, $"confirm-contact-{outcome}"),
            ],
            factory.Pushes
        );
    }

    [Fact]
    public async Task A_valid_from_date_sent_with_the_approval_is_not_recorded()
    {
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await DulyMadeAndStartedAsync(factory, client, "decide-with-date");

        var decision = await PostDecisionAsync(client, id, "approved", Ct);

        Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
        Assert.False((await factory.Persistence.GetByIdAsync(id, Ct))!.Payload.Contains("effectiveDate"));
    }

    [Fact]
    public async Task Change_valid_from_is_refused()
    {
        await using var factory = new OverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await DulyMadeAndStartedAsync(factory, client, "change-valid-from");
        Assert.Equal(HttpStatusCode.OK, (await PostDecisionAsync(client, id, "approved", effectiveDate: null, Ct)).StatusCode);
        var pending = await factory.Persistence.GetByIdAsync(id, Ct);

        var response = await client.PostAsJsonAsync(
            $"/work-items/{id}/change-valid-from",
            new { effectiveDate = "2026-09-01", reason = "Typo" },
            Ct
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("wrong-work-item-type", problem.GetProperty("errorCode").GetString());
        var after = await factory.Persistence.GetByIdAsync(id, Ct);
        Assert.Equal("approved-pending-contact", after!.StateId);
        Assert.False(after.Payload.Contains("effectiveDate"));
        Assert.Equal(pending!.AuditLog.Count, after.AuditLog.Count);
    }

    private static async Task<Guid> SubmitAsync(HttpClient client, string operatorRequestId)
    {
        var response = await client.PostAsJsonAsync(
            "/work-items",
            UpdateOverseasSiteSubmissions.Body($"{operatorRequestId}:{UpdateOverseasSiteType.Id}", siteCount: 1),
            Ct
        );
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return body.GetProperty("workItems").EnumerateArray().Single().GetProperty("id").GetGuid();
    }

    private static async Task<Guid> DulyMadeAndStartedAsync(
        OverseasSiteAppFactory factory,
        HttpClient client,
        string operatorRequestId
    )
    {
        var id = await SubmitAsync(client, operatorRequestId);
        Assert.Equal(HttpStatusCode.OK, (await PostDulyMakeAsync(client, id, paymentDate: null, Ct)).StatusCode);
        // The duly-made push lands in the background; let it settle first.
        await factory.WaitForAuditAsync(id, "status-push-sent", Ct);
        Assert.Equal(HttpStatusCode.OK, (await PostStartAssessmentAsync(client, id, Ct)).StatusCode);
        return id;
    }
}
