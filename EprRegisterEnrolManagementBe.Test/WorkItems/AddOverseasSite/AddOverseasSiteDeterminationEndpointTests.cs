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

        await factory.NumberAdapter.DidNotReceiveWithAnyArgs().GenerateOrUpdateAccreditationNumberAsync(default!, TestContext.Current.CancellationToken);
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
        var entered = EffectiveDate.ToString("yyyy-MM-dd");

        var response = await PostDecisionAsync(client, id, "approved", entered, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pending = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal(entered, pending!.Payload["effectiveDate"].AsString);
        Assert.Contains(
            pending.AuditLog,
            a => a.Action == "determination-made" && a.Details["effectiveDate"] == entered
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
    [InlineData("2025-12-31", "effective-date-before-year-start")]
    [InlineData("2000-01-01", "effective-date-before-year-start")]
    [InlineData("2027-01-01", "effective-date-after-year-end")]
    [InlineData("2099-01-01", "effective-date-after-year-end")]
    public async Task An_approval_needs_an_effective_date_inside_the_accreditation_year(
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

    // RA-795: the valid-from date may be anywhere from 1 January to 31 December
    // of the accreditation year, both ends included, and so may be before the
    // submission date (the duly-made or query-resolved date).
    [Theory]
    [InlineData("2026-01-01")]
    [InlineData("2026-12-31")]
    public async Task An_approval_accepts_the_first_and_last_day_of_the_accreditation_year(string effectiveDate)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await factory.DulyMadeAndStartedAsync(client, $"effective-date-boundary-{effectiveDate}", ct);

        var response = await PostDecisionAsync(client, id, "approved", effectiveDate, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pending = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal(effectiveDate, pending!.Payload["effectiveDate"].AsString);
        Assert.Contains(
            pending.AuditLog,
            a => a.Action == "determination-made" && a.Details["effectiveDate"] == effectiveDate
        );
    }

    [Fact]
    public async Task An_approval_may_be_valid_from_a_date_before_the_submission_date()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await factory.DulyMadeAndStartedAsync(client, "effective-date-before-submission-ok", ct);
        var item = await factory.Persistence.GetByIdAsync(id, ct);
        // A date well before this item's submission, inside its accreditation year.
        var submittedOn = UkCalendar.DateOf(item!.SubmittedAt);
        var earlier = new DateOnly(submittedOn.Year, 1, 1);
        await factory.Persistence.SetPayloadFieldAsync(id, "accreditationYear", submittedOn.Year, ct);
        var sent = earlier.ToString("yyyy-MM-dd");

        var response = await PostDecisionAsync(client, id, "approved", sent, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pending = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal(sent, pending!.Payload["effectiveDate"].AsString);
    }

    [Theory]
    [InlineData("not-a-year")]
    [InlineData("2026")]
    public async Task An_approval_is_a_server_error_when_the_accreditation_year_cannot_be_read(string unreadableYear)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await factory.DulyMadeAndStartedAsync(client, $"effective-date-no-year-{unreadableYear}", ct);
        await factory.Persistence.SetPayloadFieldAsync(id, "accreditationYear", unreadableYear, ct);

        var response = await PostDecisionAsync(client, id, "approved", "2026-06-15", ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.False(problem.TryGetProperty("errorCode", out _));
        var persisted = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal("assessment-started", persisted!.StateId);
        Assert.DoesNotContain(persisted.AuditLog, a => a.Action == "determination-made");
    }
}
