using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.AspNetCore.Mvc;
using static EprRegisterEnrolManagementBe.Test.TestSupport.AddOverseasSiteAppFactory;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.AddOverseasSite;

/// <summary>
/// RA-495 review: withdraw stays re-accreditation-only. The generic action
/// route rejects every <c>withdraw*</c> action on an add-ORS work item the
/// same way it rejects a non-caller-invocable re-accreditation query (a 400
/// "Invalid action", nothing written, nothing pushed), both for an item
/// submitted under v2 and for a v1 item once its snapshot is migrated. A
/// re-accreditation item is still withdrawn through the same route. Driven
/// through the real ASP.NET pipeline with the production modules registered,
/// against ephemeral MongoDB.
/// </summary>
public sealed class AddOverseasSiteWithdrawEndpointTests(MongoIntegrationFixture fixture)
{
    [Fact]
    public async Task Withdraw_is_rejected_on_the_generic_action_route_for_a_submitted_item()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = (await factory.SubmitAsync(client, "withdraw-submitted", siteCount: 1, ct))[0];

        await AssertGenericActionRejectedAsync(factory, client, id, "submitted", "withdraw", ct);
    }

    [Fact]
    public async Task Withdraw_is_rejected_on_the_generic_action_route_for_a_started_item()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await factory.DulyMadeAndStartedAsync(client, "withdraw-started", ct);

        await AssertGenericActionRejectedAsync(
            factory,
            client,
            id,
            "assessment-started",
            "withdraw-during-assessment",
            ct
        );
    }

    [Fact]
    public async Task Withdraw_is_not_offered_in_available_actions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = (await factory.SubmitAsync(client, "withdraw-actions", siteCount: 1, ct))[0];

        var body = await client.GetFromJsonAsync<WorkItemResponse>($"/work-items/{id}", ct);

        Assert.Equal("v2", body!.TemplateVersion);
        Assert.DoesNotContain(body.AvailableActions, a => a.ActionId.StartsWith("withdraw", StringComparison.Ordinal));
    }

    /// <summary>
    /// The guard reads each item's own frozen snapshot, so an item submitted
    /// under v1 (withdraw* caller-invocable) is only closed once the v2
    /// snapshot migration has run on it.
    /// </summary>
    [Fact]
    public async Task Withdraw_is_rejected_on_the_generic_action_route_for_an_item_migrated_from_v1()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = (await factory.SubmitAsync(client, "withdraw-v1", siteCount: 1, ct))[0];
        var v1 = await factory.Persistence.GetByIdAsync(id, ct);
        v1!.TemplateSnapshot = AddOverseasSiteWithdrawNotCallerInvocableSnapshotMigrationTests.BuildV1Snapshot();
        v1.TemplateVersion = "v1";
        await factory.Persistence.ReplaceAsync(v1, ct);

        await new AddOverseasSiteWithdrawNotCallerInvocableSnapshotMigration(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AddOverseasSiteWithdrawNotCallerInvocableSnapshotMigration>.Instance
        ).ApplyAsync(factory.Persistence, ct);

        var migrated = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal("v2", migrated!.TemplateVersion);
        Assert.False(
            Assert.Single(migrated.TemplateSnapshot!.Transitions, t => t.ActionId == "withdraw").CallerInvocable
        );

        await AssertGenericActionRejectedAsync(factory, client, id, "submitted", "withdraw", ct);
    }

    [Fact]
    public async Task A_re_accreditation_item_is_still_withdrawn_on_the_generic_action_route()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var type = new ReAccreditationType();
        var id = Guid.NewGuid();
        await factory.Persistence.CreateAsync(
            new WorkItem
            {
                Id = id,
                TypeId = ReAccreditationType.Id,
                StateId = "submitted",
                SubmittedBy = "test-client",
                TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
                TemplateVersion = type.TemplateVersion,
            },
            ct
        );

        var response = await client.PostAsync($"/work-items/{id}/actions/withdraw", content: null, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("withdrawn", (await factory.Persistence.GetByIdAsync(id, ct))!.StateId);
    }

    private static async Task AssertGenericActionRejectedAsync(
        AddOverseasSiteAppFactory factory,
        HttpClient client,
        Guid id,
        string stateId,
        string actionId,
        CancellationToken ct
    )
    {
        var before = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal(stateId, before!.StateId);
        var pushesBefore = factory.Pushes.Count;

        var response = await client.PostAsync($"/work-items/{id}/actions/{actionId}", content: null, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(ct);
        Assert.Equal("Invalid action", problem!.Title);
        Assert.Equal($"Action '{actionId}' is not declared by work item type 'add-overseas-site'.", problem.Detail);

        // Nothing happened: same state, no new audit entry, no push.
        var persisted = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal(stateId, persisted!.StateId);
        Assert.Equal(before.AuditLog.Count, persisted.AuditLog.Count);
        Assert.Equal(pushesBefore, factory.Pushes.Count);
    }
}
