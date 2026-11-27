using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.DependencyInjection;
using static EprRegisterEnrolManagementBe.Test.TestSupport.AddOverseasSiteAppFactory;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.AddOverseasSite;

/// <summary>
/// RA-800: a regulator can correct the valid-from date of an add-ORS case that is
/// Approved and awaiting applicant contact, through the shared Standard-lifecycle
/// route <c>POST /work-items/{id}/change-valid-from</c>. Only the stored date
/// changes (the status, accreditation status and due date stay, nothing is pushed:
/// Confirm applicant contact pushes whatever is stored then). Driven through the
/// real ASP.NET pipeline with the production modules registered, against ephemeral
/// MongoDB.
/// </summary>
public sealed class AddOverseasSiteChangeValidFromEndpointTests(MongoIntegrationFixture fixture)
{
    private const string NewDate = "2026-09-01";
    private const string Reason = "Typed the wrong month.";

    [Fact]
    public async Task A_valid_change_rewrites_only_the_stored_date_and_audits_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await ApprovedAwaitingContactAsync(factory, client, "change-valid-from-ok", ct);
        var before = await factory.Persistence.GetByIdAsync(id, ct);
        var pushesBefore = factory.Pushes.Count;

        var response = await PostChangeValidFromAsync(client, id, NewDate, Reason, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("approved-pending-contact", body.GetProperty("stateId").GetString());

        var after = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal(NewDate, after!.Payload["effectiveDate"].AsString);
        Assert.Equal("approved-pending-contact", after.StateId);
        Assert.Equal(before!.SlaClock!.StartedAt, after.SlaClock!.StartedAt);
        Assert.Equal(before.SlaClock.TargetDuration, after.SlaClock.TargetDuration);
        Assert.Equal(before.SlaClock.DueAt, after.SlaClock.DueAt);
        Assert.Equal(pushesBefore, factory.Pushes.Count);

        var entry = Assert.Single(after.AuditLog, a => a.Action == "valid-from-changed");
        Assert.Equal(EffectiveDate.ToString("yyyy-MM-dd"), entry.Details["previousEffectiveDate"]);
        Assert.Equal(NewDate, entry.Details["effectiveDate"]);
        Assert.Equal(Reason, entry.Details["reason"]);
        Assert.Equal("alice-1", entry.CreatedBy);
        Assert.Equal("Alice Example", entry.CreatedByName);
        Assert.Equal("approved-pending-contact", entry.StateId);
        Assert.Equal(before.AuditLog.Count + 1, after.AuditLog.Count);
        Assert.DoesNotContain(
            after.AuditLog.Skip(before.AuditLog.Count),
            a => a.Action is "action-applied" or "status-push-sent" or "sla-clock-extended"
        );
    }

    [Fact]
    public async Task Confirming_contact_afterwards_pushes_the_corrected_date()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await ApprovedAwaitingContactAsync(factory, client, "change-valid-from-push", ct);
        Assert.Equal(HttpStatusCode.OK, (await PostChangeValidFromAsync(client, id, NewDate, Reason, ct)).StatusCode);

        var confirmation = await PostConfirmContactAsync(client, id, ct);

        Assert.Equal(HttpStatusCode.OK, confirmation.StatusCode);
        var decisionPush = factory.Pushes.Last(p => p.ToStateId == "approved");
        Assert.Equal(DateOnly.Parse(NewDate), decisionPush.EffectiveDate);
        var granted = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal("approved", granted!.StateId);
    }

    [Fact]
    public async Task The_same_date_is_accepted_and_audited_with_equal_previous_and_new_dates()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await ApprovedAwaitingContactAsync(factory, client, "change-valid-from-same", ct);
        var current = EffectiveDate.ToString("yyyy-MM-dd");

        var response = await PostChangeValidFromAsync(client, id, current, Reason, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal(current, after!.Payload["effectiveDate"].AsString);
        var entry = Assert.Single(after.AuditLog, a => a.Action == "valid-from-changed");
        Assert.Equal(current, entry.Details["previousEffectiveDate"]);
        Assert.Equal(current, entry.Details["effectiveDate"]);
    }

    [Theory]
    [InlineData("2026-01-01")]
    [InlineData("2026-12-31")]
    public async Task The_first_and_last_day_of_the_accreditation_year_are_accepted(string date)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await ApprovedAwaitingContactAsync(factory, client, $"change-valid-from-bound-{date}", ct);

        var response = await PostChangeValidFromAsync(client, id, date, Reason, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal(date, after!.Payload["effectiveDate"].AsString);
    }

    [Theory]
    [InlineData(null, "effective-date-required")]
    [InlineData("", "effective-date-required")]
    [InlineData("2026-02-30", "effective-date-invalid")]
    [InlineData("10/12/2026", "effective-date-invalid")]
    [InlineData("2025-12-31", "effective-date-before-year-start")]
    [InlineData("2027-01-01", "effective-date-after-year-end")]
    public async Task A_missing_invalid_or_out_of_year_date_is_rejected_against_the_date_field(
        string? date,
        string errorCode
    )
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await ApprovedAwaitingContactAsync(
            factory,
            client,
            $"change-valid-from-{errorCode}-{date?.Replace('/', '-') ?? "absent"}",
            ct
        );
        var before = await factory.Persistence.GetByIdAsync(id, ct);

        var response = await PostChangeValidFromAsync(client, id, date, Reason, ct);

        await AssertBadRequestAsync(response, errorCode, "effectiveDate", ct);
        await AssertUnchangedAsync(factory, before!, ct);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_reason_is_rejected_against_the_reason_field(string? reason)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await ApprovedAwaitingContactAsync(factory, client, $"change-valid-from-no-reason-{reason?.Length}", ct);
        var before = await factory.Persistence.GetByIdAsync(id, ct);

        var response = await PostChangeValidFromAsync(client, id, NewDate, reason, ct);

        await AssertBadRequestAsync(response, "effective-date-reason-required", "reason", ct);
        await AssertUnchangedAsync(factory, before!, ct);
    }

    [Fact]
    public async Task A_reason_of_more_than_200_words_is_rejected_and_one_of_exactly_200_is_accepted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await ApprovedAwaitingContactAsync(factory, client, "change-valid-from-long-reason", ct);
        var before = await factory.Persistence.GetByIdAsync(id, ct);
        var twoHundred = string.Join(" ", Enumerable.Repeat("word", 200));

        var tooLong = await PostChangeValidFromAsync(client, id, NewDate, twoHundred + "\n extra", ct);
        await AssertBadRequestAsync(tooLong, "effective-date-reason-too-long", "reason", ct);
        await AssertUnchangedAsync(factory, before!, ct);

        var atLimit = await PostChangeValidFromAsync(client, id, NewDate, twoHundred, ct);
        Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);
        var after = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal(twoHundred, Assert.Single(after!.AuditLog, a => a.Action == "valid-from-changed").Details["reason"]);
    }

    [Theory]
    [InlineData("assessment-started")]
    [InlineData("rejected-pending-contact")]
    [InlineData("approved")]
    [InlineData("rejected")]
    public async Task A_case_that_is_not_approved_awaiting_contact_is_a_conflict(string state)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await factory.DulyMadeAndStartedAsync(client, $"change-valid-from-state-{state}", ct);
        await DriveToAsync(factory, client, id, state, ct);
        var before = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal(state, before!.StateId);

        var response = await PostChangeValidFromAsync(client, id, NewDate, Reason, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var after = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal(before.Version, after!.Version);
        Assert.DoesNotContain(after.AuditLog, a => a.Action == "valid-from-changed");
        Assert.Equal(
            before.Payload.TryGetValue("effectiveDate", out var was) ? was.AsString : null,
            after.Payload.TryGetValue("effectiveDate", out var now) ? now.AsString : null
        );
    }

    [Fact]
    public async Task A_request_without_an_identified_user_is_unauthorised()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await ApprovedAwaitingContactAsync(factory, client, "change-valid-from-no-user", ct);
        var before = await factory.Persistence.GetByIdAsync(id, ct);
        client.DefaultRequestHeaders.Remove("x-cdp-user-id");

        var response = await PostChangeValidFromAsync(client, id, NewDate, Reason, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertUnchangedAsync(factory, before!, ct);
    }

    [Fact]
    public async Task An_unknown_work_item_is_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();

        var response = await PostChangeValidFromAsync(client, Guid.NewGuid(), NewDate, Reason, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_concurrent_update_is_retried_and_the_change_still_lands()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture)
        {
            ConfigureTestServices = services => ConflictOnFirstReplace.Install(services),
        };
        using var client = factory.CreateClient();
        var id = await ApprovedAwaitingContactAsync(factory, client, "change-valid-from-retry", ct);
        ConflictOnFirstReplace.Arm(id);

        var response = await PostChangeValidFromAsync(client, id, NewDate, Reason, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, ConflictOnFirstReplace.Thrown);
        var after = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal(NewDate, after!.Payload["effectiveDate"].AsString);
        Assert.Single(after.AuditLog, a => a.Action == "valid-from-changed");
    }

    [Fact]
    public async Task Repeated_concurrent_updates_end_in_a_conflict_with_nothing_changed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture)
        {
            ConfigureTestServices = services => ConflictOnFirstReplace.Install(services, alwaysConflict: true),
        };
        using var client = factory.CreateClient();
        var id = await ApprovedAwaitingContactAsync(factory, client, "change-valid-from-retry-exhausted", ct);
        var before = await factory.Persistence.GetByIdAsync(id, ct);
        ConflictOnFirstReplace.Arm(id);

        var response = await PostChangeValidFromAsync(client, id, NewDate, Reason, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertUnchangedAsync(factory, before!, ct);
    }

    [Fact]
    public async Task An_unreadable_accreditation_year_is_a_server_error()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await ApprovedAwaitingContactAsync(factory, client, "change-valid-from-no-year", ct);
        await factory.Persistence.SetPayloadFieldAsync(id, "accreditationYear", "not-a-year", ct);
        var before = await factory.Persistence.GetByIdAsync(id, ct);

        var response = await PostChangeValidFromAsync(client, id, NewDate, Reason, ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await AssertUnchangedAsync(factory, before!, ct);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<Guid> ApprovedAwaitingContactAsync(
        AddOverseasSiteAppFactory factory,
        HttpClient client,
        string operatorRequestId,
        CancellationToken ct
    )
    {
        var id = await factory.DulyMadeAndStartedAsync(client, operatorRequestId, ct);
        Assert.Equal(HttpStatusCode.OK, (await PostDecisionAsync(client, id, "approved", ct)).StatusCode);
        return id;
    }

    private static async Task DriveToAsync(
        AddOverseasSiteAppFactory factory,
        HttpClient client,
        Guid id,
        string state,
        CancellationToken ct
    )
    {
        var outcome = state.StartsWith("rejected", StringComparison.Ordinal) ? "rejected" : "approved";
        if (state != "assessment-started")
        {
            Assert.Equal(HttpStatusCode.OK, (await PostDecisionAsync(client, id, outcome, ct)).StatusCode);
        }

        if (state is "approved" or "rejected")
        {
            Assert.Equal(HttpStatusCode.OK, (await PostConfirmContactAsync(client, id, ct)).StatusCode);
        }
    }

    private static Task<HttpResponseMessage> PostChangeValidFromAsync(
        HttpClient client,
        Guid id,
        string? effectiveDate,
        string? reason,
        CancellationToken ct
    ) => client.PostAsJsonAsync($"/work-items/{id}/change-valid-from", new { effectiveDate, reason }, ct);

    private static async Task AssertBadRequestAsync(
        HttpResponseMessage response,
        string errorCode,
        string field,
        CancellationToken ct
    )
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(errorCode, problem.GetProperty("errorCode").GetString());
        Assert.Equal(field, problem.GetProperty("field").GetString());
    }

    private static async Task AssertUnchangedAsync(
        AddOverseasSiteAppFactory factory,
        WorkItem before,
        CancellationToken ct
    )
    {
        var after = await factory.Persistence.GetByIdAsync(before.Id, ct);
        Assert.Equal(before.Version, after!.Version);
        Assert.Equal(before.Payload["effectiveDate"].AsString, after.Payload["effectiveDate"].AsString);
        Assert.DoesNotContain(after.AuditLog, a => a.Action == "valid-from-changed");
    }

    /// <summary>
    /// Fault injection: the first (or, with <c>alwaysConflict</c>, every) write to the
    /// armed work item throws the optimistic-concurrency exception a competing writer
    /// would cause. Static because the factory builds the decorator itself.
    /// </summary>
    private sealed class ConflictOnFirstReplace(IWorkItemPersistence inner, bool alwaysConflict)
        : ForwardingWorkItemPersistence(inner)
    {
        private static Guid s_armed;
        private static int s_thrown;

        public static int Thrown => s_thrown;

        public static void Arm(Guid id)
        {
            s_armed = id;
            s_thrown = 0;
        }

        public static void Install(IServiceCollection services, bool alwaysConflict = false)
        {
            var real = services.Last(d => d.ServiceType == typeof(IWorkItemPersistence));
            services.Remove(real);
            services.AddSingleton<IWorkItemPersistence>(sp =>
                new ConflictOnFirstReplace(
                    (IWorkItemPersistence)(
                        real.ImplementationInstance
                        ?? real.ImplementationFactory?.Invoke(sp)
                        ?? ActivatorUtilities.CreateInstance(sp, real.ImplementationType!)
                    ),
                    alwaysConflict
                )
            );
        }

        public override Task ReplaceAsync(WorkItem workItem, CancellationToken cancellationToken = default)
        {
            if (workItem.Id == s_armed && (alwaysConflict || s_thrown == 0))
            {
                Interlocked.Increment(ref s_thrown);
                throw new WorkItemConcurrencyException(workItem.Id, workItem.Version);
            }

            return Inner.ReplaceAsync(workItem, cancellationToken);
        }
    }
}
