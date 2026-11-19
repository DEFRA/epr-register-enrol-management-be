using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using NSubstitute;
using static EprRegisterEnrolManagementBe.Test.TestSupport.AddOverseasSiteAppFactory;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.AddOverseasSite;

/// <summary>
/// RA-495 (AC5 to AC8): a Duly made add-ORS work item takes a final
/// determination (Approved, with the AC6 effective date, or Refused) and then the applicant-contact
/// confirmation, through the shared Standard-lifecycle routes
/// (<c>POST /work-items/{id}/decision</c> and <c>/confirm-contact</c>).
/// Approval mints nothing (the accreditation already exists) and no email is
/// sent. The operator-journey pushes are covered by
/// AddOverseasSiteStatusPushEndpointTests. Driven through the real ASP.NET
/// pipeline with the production modules registered, against ephemeral
/// MongoDB.
/// </summary>
public sealed class AddOverseasSiteDeterminationEndpointTests(MongoIntegrationFixture fixture)
{
    [Fact]
    public async Task Approving_then_confirming_contact_grants_the_site_without_minting_a_number_or_sending_an_email()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await factory.DulyMadeAndStartedAsync(client, "determine-approve", ct);

        var determination = await PostDecisionAsync(client, id, "approved", ct);

        Assert.Equal(HttpStatusCode.OK, determination.StatusCode);
        var pending = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal("approved-pending-contact", pending!.StateId);
        Assert.Contains(pending.AuditLog, a => a.Action == "determination-made" && a.Details["outcome"] == "approved");
        Assert.Equal(EffectiveDate.ToString("yyyy-MM-dd"), pending.Payload["effectiveDate"].AsString);

        var confirmation = await PostConfirmContactAsync(client, id, ct);

        Assert.Equal(HttpStatusCode.OK, confirmation.StatusCode);
        var granted = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal("approved", granted!.StateId);
        Assert.Contains(granted.AuditLog, a => a.Action == "applicant-contacted" && a.Details["outcome"] == "approved");
        Assert.Contains(granted.AuditLog, a => a.Action == "sla-clock-stopped");
        Assert.False(granted.Payload.Contains("accreditationId"));

        await factory.NumberAdapter.DidNotReceiveWithAnyArgs().GenerateOrUpdateAccreditationNumberAsync(default!, default);
        Assert.Empty(factory.NotifyClient.ReceivedCalls());
    }

    [Fact]
    public async Task Refusing_then_confirming_contact_closes_the_case_as_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await factory.DulyMadeAndStartedAsync(client, "determine-refuse", ct);

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
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    public async Task The_determination_is_final(string outcome)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await factory.DulyMadeAndStartedAsync(client, $"determine-final-{outcome}", ct);
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
        var id = await factory.DulyMadeAndStartedAsync(client, "confirm-too-early", ct);

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
        var id = await factory.DulyMadeAndStartedAsync(client, $"closed-assign-{outcome}", ct);
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

    [Fact]
    public async Task Approving_records_the_entered_effective_date_in_the_application_history()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await factory.DulyMadeAndStartedAsync(client, "effective-date-history", ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

        var response = await PostDecisionAsync(client, id, "approved", today, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pending = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal(today, pending!.Payload["effectiveDate"].AsString);
        Assert.Contains(
            pending.AuditLog,
            a => a.Action == "determination-made" && a.Details["effectiveDate"] == today
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2099-01-01")]
    public async Task Refusing_records_the_submission_date_as_the_effective_date(string? sentDate)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await factory.DulyMadeAndStartedAsync(client, $"effective-date-refusal-{sentDate ?? "absent"}", ct);

        var response = await PostDecisionAsync(client, id, "rejected", sentDate, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pending = await factory.Persistence.GetByIdAsync(id, ct);
        var submittedOn = UkCalendar.DateOf(pending!.SubmittedAt).ToString("yyyy-MM-dd");
        Assert.Equal(submittedOn, pending.Payload["effectiveDate"].AsString);
        Assert.Contains(
            pending.AuditLog,
            a => a.Action == "determination-made" && a.Details["effectiveDate"] == submittedOn
        );
    }

    [Theory]
    [InlineData(null, "effective-date-required")]
    [InlineData("", "effective-date-required")]
    [InlineData("2026-02-30", "effective-date-invalid")]
    [InlineData("10/12/2026", "effective-date-invalid")]
    [InlineData("2000-01-01", "effective-date-before-submission")]
    public async Task An_approval_needs_an_effective_date_on_or_after_the_submission_date(
        string? effectiveDate,
        string errorCode
    )
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await factory.DulyMadeAndStartedAsync(
            client,
            $"effective-date-{errorCode}-{effectiveDate?.Replace('/', '-') ?? "absent"}",
            ct
        );

        var response = await PostDecisionAsync(client, id, "approved", effectiveDate, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(errorCode, problem.GetProperty("errorCode").GetString());
        Assert.Equal("effectiveDate", problem.GetProperty("field").GetString());
        var persisted = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal("assessment-started", persisted!.StateId);
        Assert.False(persisted.Payload.Contains("effectiveDate"));
        Assert.DoesNotContain(persisted.AuditLog, a => a.Action == "determination-made");
    }
}
