using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.AddOverseasSite;

/// <summary>
/// RA-495 (AC5 to AC8): a Duly made add-ORS work item takes a final
/// determination (Approved or Refused) and then the applicant-contact
/// confirmation, through the shared Standard-lifecycle routes
/// (<c>POST /work-items/{id}/decision</c> and <c>/confirm-contact</c>).
/// Approval mints nothing (the accreditation already exists) and no email is
/// sent. add-ORS has no operator-journey push target yet, so nothing is
/// pushed. Driven through the real ASP.NET pipeline with the production
/// modules registered, against ephemeral MongoDB.
/// </summary>
public sealed class AddOverseasSiteDeterminationEndpointTests(MongoIntegrationFixture fixture)
{
    private static readonly DateOnly s_paymentDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3);

    [Fact]
    public async Task Approving_then_confirming_contact_grants_the_site_without_minting_a_number_or_sending_an_email()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await DulyMadeAndStartedAsync(factory, client, "determine-approve", ct);

        var determination = await PostDecisionAsync(client, id, "approved", ct);

        Assert.Equal(HttpStatusCode.OK, determination.StatusCode);
        var pending = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal("approved-pending-contact", pending!.StateId);
        Assert.Contains(pending.AuditLog, a => a.Action == "determination-made" && a.Details["outcome"] == "approved");

        var confirmation = await PostConfirmContactAsync(client, id, ct);

        Assert.Equal(HttpStatusCode.OK, confirmation.StatusCode);
        var granted = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal("approved", granted!.StateId);
        Assert.Contains(granted.AuditLog, a => a.Action == "applicant-contacted" && a.Details["outcome"] == "approved");
        Assert.Contains(granted.AuditLog, a => a.Action == "sla-clock-stopped");
        Assert.False(granted.Payload.Contains("accreditationId"));

        await factory.NumberAdapter.DidNotReceiveWithAnyArgs().GenerateOrUpdateAccreditationNumberAsync(default!, default);
        Assert.Empty(factory.NotifyClient.ReceivedCalls());
        await factory.AssertNothingPushedAsync(id, ct);
    }

    [Fact]
    public async Task Refusing_then_confirming_contact_closes_the_case_as_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await DulyMadeAndStartedAsync(factory, client, "determine-refuse", ct);

        var determination = await PostDecisionAsync(client, id, "rejected", ct);

        Assert.Equal(HttpStatusCode.OK, determination.StatusCode);
        var pending = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal("rejected-pending-contact", pending!.StateId);
        Assert.Contains(pending.AuditLog, a => a.Action == "determination-made" && a.Details["outcome"] == "rejected");

        var confirmation = await PostConfirmContactAsync(client, id, ct);

        Assert.Equal(HttpStatusCode.OK, confirmation.StatusCode);
        var refused = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal("rejected", refused!.StateId);
        Assert.Contains(refused.AuditLog, a => a.Action == "applicant-contacted" && a.Details["outcome"] == "rejected");
        Assert.Empty(factory.NotifyClient.ReceivedCalls());
        await factory.AssertNothingPushedAsync(id, ct);
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    public async Task The_determination_is_final(string outcome)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await DulyMadeAndStartedAsync(factory, client, $"determine-final-{outcome}", ct);
        Assert.Equal(HttpStatusCode.OK, (await PostDecisionAsync(client, id, outcome, ct)).StatusCode);
        var other = outcome == "approved" ? "rejected" : "approved";

        var whilePending = await PostDecisionAsync(client, id, other, ct);
        Assert.Equal(HttpStatusCode.OK, (await PostConfirmContactAsync(client, id, ct)).StatusCode);
        var onceClosed = await PostDecisionAsync(client, id, other, ct);

        Assert.Equal(HttpStatusCode.Conflict, whilePending.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, onceClosed.StatusCode);
        var closed = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal(outcome, closed!.StateId);
        Assert.Single(closed.AuditLog, a => a.Action == "determination-made");
    }

    [Fact]
    public async Task No_determination_is_taken_before_the_work_item_is_duly_made()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var ids = await factory.SubmitAsync(client, "determine-not-started", siteCount: 1, ct);

        var response = await PostDecisionAsync(client, ids[0], "approved", ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(ids[0], ct);
        Assert.Equal("submitted", persisted!.StateId);
        Assert.DoesNotContain(persisted.AuditLog, a => a.Action == "determination-made");
    }

    [Fact]
    public async Task Contact_is_not_confirmed_before_a_determination()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await DulyMadeAndStartedAsync(factory, client, "confirm-too-early", ct);

        var response = await PostConfirmContactAsync(client, id, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal("assessment-started", persisted!.StateId);
        Assert.DoesNotContain(persisted.AuditLog, a => a.Action == "applicant-contacted");
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    public async Task A_closed_case_cannot_be_reassigned_or_unassigned(string outcome)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await DulyMadeAndStartedAsync(factory, client, $"closed-assign-{outcome}", ct);
        Assert.Equal(HttpStatusCode.OK, (await PostDecisionAsync(client, id, outcome, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostConfirmContactAsync(client, id, ct)).StatusCode);
        var closed = await factory.Persistence.GetByIdAsync(id, ct);

        var assign = await client.PostAsJsonAsync(
            $"/work-items/{id}/assign",
            new { assigneeId = "bob-2", assigneeName = "Bob Example" },
            ct
        );
        var unassign = await client.PostAsync($"/work-items/{id}/unassign", content: null, ct);

        Assert.Equal(HttpStatusCode.Conflict, assign.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, unassign.StatusCode);
        var after = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal(closed!.Version, after!.Version);
        Assert.Equal(closed.AssignedToId, after.AssignedToId);
    }

    /// <summary>
    /// A one-site submission, duly made and with its assessment started, as
    /// the front end leaves it before posting a determination.
    /// </summary>
    private static async Task<Guid> DulyMadeAndStartedAsync(
        AddOverseasSiteAppFactory factory,
        HttpClient client,
        string operatorRequestId,
        CancellationToken ct
    )
    {
        var id = (await factory.SubmitAsync(client, operatorRequestId, siteCount: 1, ct))[0];
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await client.PostAsJsonAsync(
                    $"/work-items/{id}/duly-make",
                    new { paymentDate = s_paymentDate.ToString("yyyy-MM-dd") },
                    ct
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsync($"/work-items/{id}/actions/start-assessment", content: null, ct)).StatusCode
        );
        return id;
    }

    private static Task<HttpResponseMessage> PostDecisionAsync(
        HttpClient client,
        Guid id,
        string outcome,
        CancellationToken ct
    ) => client.PostAsJsonAsync($"/work-items/{id}/decision", new { outcome }, ct);

    private static Task<HttpResponseMessage> PostConfirmContactAsync(HttpClient client, Guid id, CancellationToken ct) =>
        client.PostAsync($"/work-items/{id}/confirm-contact", content: null, ct);
}
