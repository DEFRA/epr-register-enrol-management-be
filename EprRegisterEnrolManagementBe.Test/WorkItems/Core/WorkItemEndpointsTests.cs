using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// epr-efp: persistence is real <see cref="WorkItemPersistence"/> backed
/// by ephemeral MongoDB. Tests seed work items via
/// <see cref="TestApplicationFactory.Persistence"/> and assert on
/// fetched-back documents rather than on a captured-in-flight reference.
/// A thin <see cref="RecordingPersistence"/> wrapper is layered on top
/// for the handful of tests that still need to inspect the
/// <see cref="WorkItemQuery"/> the endpoint built — the wrapper records
/// calls but delegates to the real persistence so behaviour stays end-to-end.
/// </summary>
public class WorkItemEndpointsTests
    : IClassFixture<MongoIntegrationFixture>
{
    private const string TypeId = "test-type";
    private readonly MongoIntegrationFixture _fixture;

    public WorkItemEndpointsTests(MongoIntegrationFixture fixture) => _fixture = fixture;

    private TestApplicationFactory NewFactory(
        bool includeAuthHeader = true,
        string? userRoles = null,
        string? userId = "test-user",
        string? userName = null,
        Dictionary<string, IReadOnlyCollection<WorkItemTask>>? tasksByState = null) =>
        new(_fixture, includeAuthHeader, userRoles, userId, userName, tasksByState);

    [Fact]
    public async Task Post_returns_unauthorized_without_cognito_client_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory(includeAuthHeader: false);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/work-items", new { typeId = TypeId }, cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_returns_problem_when_typeId_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/work-items", new { typeId = string.Empty }, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Equal("Invalid request", problem?.Title);
    }

    [Fact]
    public async Task Post_returns_problem_when_typeId_unknown()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/work-items", new { typeId = "unknown-type" }, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Equal("Unknown work item type", problem?.Title);
    }

    [Fact]
    public async Task Post_persists_work_item_in_initial_state_with_payload_and_submitter()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/work-items", new
        {
            typeId = TypeId,
            payload = new { applicantName = "Acme", tonnage = 42 }
        }, cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkItemResponse>(cancellationToken);
        Assert.NotNull(body);

        var persisted = await factory.Persistence.GetByIdAsync(body!.Id, cancellationToken);
        Assert.NotNull(persisted);
        Assert.Equal(TypeId, persisted!.TypeId);
        Assert.Equal("submitted", persisted.StateId);
        Assert.Equal("test-client", persisted.SubmittedBy);
        Assert.Equal("Acme", persisted.Payload["applicantName"].AsString);
        Assert.Equal(42, persisted.Payload["tonnage"].AsInt32);

        // Snapshot of the type's template (states, tasks, transitions, version)
        // is frozen onto the work item at submission for faithful historical
        // rendering after the live module changes.
        Assert.NotNull(persisted.TemplateSnapshot);
        Assert.Equal("v1", persisted.TemplateVersion);
        Assert.Equal("v1", persisted.TemplateSnapshot!.TemplateVersion);

        Assert.NotNull(response.Headers.Location);
        Assert.StartsWith("/work-items/", response.Headers.Location!.AbsolutePath);

        Assert.Equal(TypeId, body.TypeId);
        Assert.Equal("submitted", body.StateId);
        Assert.Equal("test-client", body.SubmittedBy);
        Assert.Equal("v1", body.TemplateVersion);
        Assert.Equal(JsonValueKind.Object, body.Payload.ValueKind);
        Assert.Equal("Acme", body.Payload.GetProperty("applicantName").GetString());
    }

    [Fact]
    public async Task Post_records_work_item_submitted_audit_entry_as_birth_event()
    {
        // RA-97: the audit timeline must start at submission. The framework
        // appends one 'work-item-submitted' entry inside the same
        // CreateAsync call that persists the new document, attributed to
        // the forwarded user:id and stamped with WorkItem.SubmittedAt.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory(userId: "alice-1", userName: "Alice Example");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/work-items", new { typeId = TypeId }, cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkItemResponse>(cancellationToken);
        var persisted = await factory.Persistence.GetByIdAsync(body!.Id, cancellationToken);
        Assert.NotNull(persisted);
        var entry = Assert.Single(persisted!.AuditLog);
        Assert.Equal("work-item-submitted", entry.Action);
        Assert.Equal("Work item submitted", entry.ActionDisplayName);
        Assert.Equal("alice-1", entry.CreatedBy);
        Assert.Equal("Alice Example", entry.CreatedByName);
        Assert.Equal(persisted.SubmittedAt, entry.CreatedAt);
        Assert.Equal(TypeId, entry.Details["typeId"]);
        Assert.Equal("submitted", entry.Details["stateId"]);
        // Birth event was part of the original CreateAsync (no follow-up
        // ReplaceAsync), so Version is still 0.
        Assert.Equal(0, persisted.Version);
    }

    [Fact]
    public async Task Post_returns_401_and_persists_nothing_when_user_id_claim_missing()
    {
        // RA-97 / engine rule: every mutating call requires the BFF to
        // forward a 'user:id' claim so the audit entry can be tied back
        // to a real human. Without it the engine refuses to write.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory(userId: null);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/work-items", new { typeId = TypeId }, cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await factory.AllItemsAsync(cancellationToken));
    }

    [Fact]
    public async Task Post_records_submission_metadata_on_birth_audit_entry()
    {
        // RA-126: birth audit entry must carry source / clientId / userId
        // / applicationReference alongside the existing typeId / stateId
        // / templateVersion keys so audit consumers can reconstruct the
        // submission origin without joining back to request logs.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory(userId: "alice-1", userName: "Alice Example");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/work-items", new
        {
            typeId = TypeId,
            source = "operator-fe",
            applicationReference = "APP-123",
            payload = new { applicantName = "Acme" }
        }, cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkItemResponse>(cancellationToken);
        var persisted = await factory.Persistence.GetByIdAsync(body!.Id, cancellationToken);
        Assert.NotNull(persisted);
        var entry = Assert.Single(persisted!.AuditLog);
        Assert.Equal("work-item-submitted", entry.Action);
        Assert.Equal(TypeId, entry.Details["typeId"]);
        Assert.Equal("submitted", entry.Details["stateId"]);
        Assert.Equal("v1", entry.Details["templateVersion"]);
        Assert.Equal("operator-fe", entry.Details["source"]);
        Assert.Equal("test-client", entry.Details["clientId"]);
        Assert.Equal("alice-1", entry.Details["userId"]);
        Assert.Equal("APP-123", entry.Details["applicationReference"]);
    }

    [Fact]
    public async Task Post_records_birth_audit_entry_with_null_metadata_when_omitted()
    {
        // RA-126: source / applicationReference are optional; when the
        // caller omits them the keys still appear on Details with a
        // null value so audit consumers can rely on a fixed shape.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory(userId: "alice-1");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/work-items", new { typeId = TypeId }, cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkItemResponse>(cancellationToken);
        var persisted = await factory.Persistence.GetByIdAsync(body!.Id, cancellationToken);
        var entry = Assert.Single(persisted!.AuditLog);
        Assert.True(entry.Details.ContainsKey("source"));
        Assert.Null(entry.Details["source"]);
        Assert.True(entry.Details.ContainsKey("applicationReference"));
        Assert.Null(entry.Details["applicationReference"]);
        Assert.Equal("test-client", entry.Details["clientId"]);
        Assert.Equal("alice-1", entry.Details["userId"]);
    }

    [Theory]
    [InlineData("source", "'source' must be a string.")]
    [InlineData("applicationReference", "'applicationReference' must be a string.")]
    public async Task Post_rejects_non_string_submission_metadata_field(string field, string expectedDetail)
    {
        // RA-126: reject malformed metadata up front rather than
        // silently coercing / dropping it — that would leave the audit
        // record degraded without the caller noticing.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        // Hand-craft body to inject a non-string value (e.g. a number)
        // for the field under test.
        var json = $$"""
        { "typeId": "{{TypeId}}", "{{field}}": 42 }
        """;
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/work-items", content, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Equal("Invalid request body", problem?.Title);
        Assert.Equal(expectedDetail, problem?.Detail);
        Assert.Empty(await factory.AllItemsAsync(cancellationToken));
    }

    [Fact]
    public async Task Post_accepts_request_without_payload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/work-items", new { typeId = TypeId }, cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkItemResponse>(cancellationToken);
        var persisted = await factory.Persistence.GetByIdAsync(body!.Id, cancellationToken);
        Assert.NotNull(persisted);
        Assert.Empty(persisted!.Payload);
    }

    [Fact]
    public async Task Post_returns_problem_when_payload_is_not_an_object()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/work-items", new
        {
            typeId = TypeId,
            payload = new[] { 1, 2, 3 }
        }, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Equal("Invalid work item payload", problem?.Title);
    }

    [Fact]
    public async Task Get_by_id_returns_work_item_when_present()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        }, cancellationToken);

        var response = await client.GetAsync($"/work-items/{id}", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<WorkItemResponse>(cancellationToken);
        Assert.Equal(id, body?.Id);
        Assert.Equal(TypeId, body?.TypeId);
    }

    [Fact]
    public async Task Get_by_id_returns_not_found_when_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/work-items/{Guid.NewGuid()}", cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_by_id_returns_not_found_for_cross_tenant_access()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "other-tenant"
        }, cancellationToken);

        var response = await client.GetAsync($"/work-items/{id}", cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_by_id_allows_case_worker_to_read_any_tenants_item()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory(userRoles: WorkItemEndpoints.CaseWorkerRole);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "other-tenant"
        }, cancellationToken);

        var response = await client.GetAsync($"/work-items/{id}", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_list_filters_by_caller_client_id_when_not_case_worker()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        // Seed two items: one belongs to the caller, one to another tenant.
        // The non-case-worker filter must only surface the caller's.
        await factory.SeedAsync(new WorkItem
        {
            Id = Guid.NewGuid(),
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        }, cancellationToken);
        await factory.SeedAsync(new WorkItem
        {
            Id = Guid.NewGuid(),
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "other-tenant"
        }, cancellationToken);

        var body = await client.GetFromJsonAsync<WorkItemListResponse>("/work-items", cancellationToken);
        Assert.NotNull(body);
        var item = Assert.Single(body!.Items);
        Assert.Equal("test-client", item.SubmittedBy);

        // Defence in depth: the captured query carried the SubmittedBy
        // filter, so the gate is enforced upstream of Mongo not as a
        // post-filter (the latter would be a much more dangerous design).
        var lastQuery = factory.Recording.LastQuery;
        Assert.NotNull(lastQuery);
        Assert.Equal("test-client", lastQuery!.SubmittedBy);
    }

    [Fact]
    public async Task Get_list_does_not_filter_when_case_worker()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory(userRoles: WorkItemEndpoints.CaseWorkerRole);
        using var client = factory.CreateClient();

        await factory.SeedAsync(new WorkItem
        {
            Id = Guid.NewGuid(),
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        }, cancellationToken);
        await factory.SeedAsync(new WorkItem
        {
            Id = Guid.NewGuid(),
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "other-tenant"
        }, cancellationToken);

        var body = await client.GetFromJsonAsync<WorkItemListResponse>("/work-items", cancellationToken);
        Assert.NotNull(body);
        Assert.Equal(2, body!.Items.Count);

        var lastQuery = factory.Recording.LastQuery;
        Assert.NotNull(lastQuery);
        Assert.Null(lastQuery!.SubmittedBy);
    }

    [Fact]
    public async Task Get_list_returns_empty_page_without_querying_persistence_when_caller_has_no_client_id()
    {
        // epr-z0k: standard callers with no identifiable submitter id
        // (no cognito:client_id claim AND no NameIdentifier) must
        // structurally see nothing. Previously the endpoint funnelled
        // them through Mongo with a magic SubmittedBy = '__no_tenant__'
        // sentinel; now it short-circuits with an empty page so the
        // gate cannot fail open if a future submitter id ever happened
        // to match the literal sentinel.
        var cancellationToken = TestContext.Current.CancellationToken;
        var persistence = Substitute.For<IWorkItemPersistence>();
        var engine = Substitute.For<IWorkItemService>();
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(authenticationType: "test"))
        };

        var result = await WorkItemEndpoints.GetAll(httpContext, persistence, engine, TimeProvider.System, cancellationToken);

        var ok = Assert.IsType<Ok<WorkItemListResponse>>(result.Result);
        Assert.NotNull(ok.Value);
        Assert.Empty(ok.Value!.Items);
        Assert.Equal(0, ok.Value.TotalCount);
        Assert.Equal(1, ok.Value.Page);
        Assert.Equal(WorkItemQuery.DefaultPageSize, ok.Value.PageSize);
        // The structural property under test is "persistence is never
        // consulted" — verifying it requires a stub that records calls,
        // not a real database. Kept as a substitute deliberately.
        await persistence.DidNotReceiveWithAnyArgs().QueryAsync(default!, cancellationToken);
    }

    [Fact]
    public async Task Get_list_does_not_expose_items_submitted_by_literal_sentinel_to_no_tenant_caller()
    {
        // epr-z0k regression guard: even if some upstream submitter
        // somehow ended up with the historical '__no_tenant__' literal
        // as their cognito:client_id, a caller with no client_id of
        // their own must NOT inherit visibility of those items.
        // Structurally enforced by the short-circuit: persistence is
        // never asked.
        var cancellationToken = TestContext.Current.CancellationToken;
        var persistence = Substitute.For<IWorkItemPersistence>();
        var engine = Substitute.For<IWorkItemService>();
        persistence
            .QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage(
                Items: new List<WorkItem>
                {
                    new() { TypeId = TypeId, StateId = "submitted", SubmittedBy = "__no_tenant__" }
                },
                TotalCount: 1,
                Page: 1,
                PageSize: WorkItemQuery.DefaultPageSize));
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(authenticationType: "test"))
        };

        var result = await WorkItemEndpoints.GetAll(httpContext, persistence, engine, TimeProvider.System, cancellationToken);

        var ok = Assert.IsType<Ok<WorkItemListResponse>>(result.Result);
        Assert.Empty(ok.Value!.Items);
        Assert.Equal(0, ok.Value.TotalCount);
        await persistence.DidNotReceiveWithAnyArgs().QueryAsync(default!, cancellationToken);
    }

    [Fact]
    public async Task Get_list_rejects_page_above_cap_with_400()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/work-items?page={WorkItemQuery.MaxPage + 1}", cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // Persistence must never be hit for an out-of-range page — that's the
        // whole point of the cap.
        Assert.Null(factory.Recording.LastQuery);
    }

    [Fact]
    public async Task Get_returns_paginated_envelope_for_all_persisted_work_items()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        await factory.SeedAsync(new WorkItem
        {
            Id = Guid.NewGuid(),
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        }, cancellationToken);
        await factory.SeedAsync(new WorkItem
        {
            Id = Guid.NewGuid(),
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        }, cancellationToken);

        var body = await client.GetFromJsonAsync<WorkItemListResponse>("/work-items", cancellationToken);
        Assert.NotNull(body);
        Assert.Equal(2, body!.Items.Count);
        Assert.Equal(2, body.TotalCount);
        Assert.Equal(1, body.Page);
        Assert.Equal(WorkItemQuery.DefaultPageSize, body.PageSize);
    }

    [Fact]
    public async Task Get_list_omits_notes_and_audit_log_from_each_item()
    {
        // epr-4pf: the list endpoint never renders the per-item Notes /
        // AuditLog collections, so they must not appear on the wire at
        // all (omitted, not nulled) — even if the persisted document
        // would otherwise have them.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var assignedAt = new DateTime(2026, 4, 27, 9, 0, 0, DateTimeKind.Utc);
        var workItem = new WorkItem
        {
            Id = Guid.NewGuid(),
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client",
            AssignedToId = "alice-1",
            AssignedToName = "Alice Example",
            AssignedAt = assignedAt,
            AssignedBy = "manager-1",
            Notes =
            {
                new WorkItemNote { Text = "should not be on the wire", CreatedAt = assignedAt, CreatedBy = "alice-1" }
            },
            AuditLog =
            {
                new WorkItemAuditEntry
                {
                    Action = "assigned",
                    ActionDisplayName = "Assigned",
                    CreatedAt = assignedAt,
                    CreatedBy = "manager-1"
                }
            }
        };
        await factory.SeedAsync(workItem, cancellationToken);

        var response = await client.GetAsync("/work-items", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Property-level assertion: the JSON must not even mention
        // 'notes' or 'auditLog' on a list item.
        var rawJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var root = System.Text.Json.Nodes.JsonNode.Parse(rawJson);
        Assert.NotNull(root);
        var firstItem = root!["items"]!.AsArray()[0]!.AsObject();
        Assert.False(firstItem.ContainsKey("notes"), "list item must not include 'notes'");
        Assert.False(firstItem.ContainsKey("auditLog"), "list item must not include 'auditLog'");

        var body = System.Text.Json.JsonSerializer.Deserialize<WorkItemListResponse>(
            rawJson, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
        Assert.NotNull(body);
        var item = Assert.Single(body!.Items);
        Assert.Equal("alice-1", item.AssignedToId);
        Assert.Equal("Alice Example", item.AssignedToName);
        Assert.Equal(assignedAt, item.AssignedAt);
        Assert.Equal("manager-1", item.AssignedBy);
        Assert.Equal("submitted", item.StateId);
    }

    [Fact]
    public async Task Get_passes_query_string_filters_to_persistence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            "/work-items?typeId=re-accreditation&typeId=other-type&stateId=submitted&search=acme&assigneeId=alice-1&unassigned=true&page=2&pageSize=5",
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var captured = factory.Recording.LastQuery;
        Assert.NotNull(captured);
        Assert.NotNull(captured!.TypeIds);
        Assert.Equal(new[] { "re-accreditation", "other-type" }, captured.TypeIds);
        Assert.Equal(new[] { "submitted" }, captured.StateIds);
        Assert.Equal("acme", captured.Search);
        Assert.Equal("alice-1", captured.AssigneeId);
        Assert.True(captured.UnassignedOnly);
        Assert.Equal(2, captured.Page);
        Assert.Equal(5, captured.PageSize);
    }

    [Fact]
    public async Task Assign_persists_assignee_snapshot_and_returns_updated_response()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory(userRoles: "standard,assign", userId: "actor-1");
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        }, cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/{id}/assign",
            new { assigneeId = "alice-1", assigneeName = "Alice Example" },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkItemResponse>(cancellationToken);
        Assert.Equal("alice-1", body?.AssignedToId);
        Assert.Equal("Alice Example", body?.AssignedToName);
        Assert.Equal("actor-1", body?.AssignedBy);
        Assert.NotNull(body?.AssignedAt);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("alice-1", persisted!.AssignedToId);
        Assert.Equal(1, persisted.Version);
    }

    [Fact]
    public async Task Assign_returns_400_when_assigneeId_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory(userRoles: "standard,assign", userId: "actor-1");
        using var client = factory.CreateClient();

        // The cross-tenant gate (epr-0t9) loads the work item before
        // any body validation runs, so the gate must be satisfied with a
        // matching SubmittedBy or the test sees 404 instead of the 400
        // it cares about. Caller's cognito client id is 'test-client'.
        var id = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        }, cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/{id}/assign", new { }, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Assign_returns_403_when_standard_user_assigns_to_someone_else()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory(userRoles: "standard", userId: "alice-1");
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        }, cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/{id}/assign", new { assigneeId = "bob-1", assigneeName = "Bob" }, cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Assign_returns_404_when_work_item_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory(userRoles: "standard,assign", userId: "actor-1");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/work-items/{Guid.NewGuid()}/assign",
            new { assigneeId = "alice-1", assigneeName = "Alice" },
            cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unassign_clears_assignment_when_actor_has_assign_role()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory(userRoles: "standard,assign", userId: "actor-1");
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client",
            AssignedToId = "alice-1",
            AssignedToName = "Alice",
            AssignedAt = new DateTime(2026, 4, 27, 9, 0, 0, DateTimeKind.Utc),
            AssignedBy = "earlier-actor"
        }, cancellationToken);

        var response = await client.PostAsync($"/work-items/{id}/unassign", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkItemResponse>(cancellationToken);
        Assert.Null(body?.AssignedToId);
        Assert.Null(body?.AssignedToName);
        Assert.Null(body?.AssignedAt);
        Assert.Null(body?.AssignedBy);
    }

    [Fact]
    public async Task Unassign_returns_403_when_caller_lacks_assign_role()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory(userRoles: "standard", userId: "alice-1");
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client",
            AssignedToId = "alice-1"
        }, cancellationToken);

        var response = await client.PostAsync($"/work-items/{id}/unassign", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Assign_to_same_user_sets_idempotent_replay_header_and_does_not_persist()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory(userRoles: "standard,assign", userId: "actor-1");
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client",
            AssignedToId = "alice-1",
            AssignedToName = "Alice Example",
            AssignedAt = new DateTime(2026, 4, 27, 9, 0, 0, DateTimeKind.Utc),
            AssignedBy = "earlier-actor"
        }, cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/{id}/assign",
            new { assigneeId = "alice-1", assigneeName = "Alice Example" },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Idempotent-Replay", out var values));
        Assert.Equal("true", Assert.Single(values!));
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal(0, persisted!.Version);
    }

    [Fact]
    public async Task Unassign_already_unassigned_sets_idempotent_replay_header_and_does_not_persist()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory(userRoles: "standard,assign", userId: "actor-1");
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        }, cancellationToken);

        var response = await client.PostAsync($"/work-items/{id}/unassign", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Idempotent-Replay", out var values));
        Assert.Equal("true", Assert.Single(values!));
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal(0, persisted!.Version);
    }

    [Fact]
    public async Task AddNote_persists_note_and_returns_updated_response()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory(userId: "alice-1", userName: "Alice Example");
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        }, cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/{id}/notes",
            new { text = "Reviewed evidence; awaiting confirmation." },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkItemResponse>(cancellationToken);
        Assert.NotNull(body?.Notes);
        var note = Assert.Single(body!.Notes!);
        Assert.Equal("Reviewed evidence; awaiting confirmation.", note.Text);
        Assert.Equal("alice-1", note.CreatedBy);
        Assert.Equal("Alice Example", note.CreatedByName);

        // epr-27o: the audit-log entry on the wire surfaces the trimmed
        // note body via Details["noteText"] so a UI rendering the
        // timeline does not have to cross-reference the Notes
        // collection by id.
        Assert.NotNull(body.AuditLog);
        var noteAudit = Assert.Single(body.AuditLog!, a => a.Action == "note-added");
        Assert.Equal("Reviewed evidence; awaiting confirmation.", noteAudit.Details["noteText"]);
        Assert.Equal(note.Id.ToString(), noteAudit.Details["noteId"]);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Single(persisted!.Notes);
        Assert.Equal(1, persisted.Version);
    }

    [Fact]
    public async Task AddNote_projects_notes_newest_first()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory(userId: "alice-1");
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var older = new DateTime(2026, 4, 27, 9, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 4, 27, 11, 0, 0, DateTimeKind.Utc);
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client",
            Notes =
            {
                new WorkItemNote { Text = "older", CreatedAt = older, CreatedBy = "earlier" },
                new WorkItemNote { Text = "newer", CreatedAt = newer, CreatedBy = "earlier" }
            }
        }, cancellationToken);

        var response = await client.GetAsync($"/work-items/{id}", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<WorkItemResponse>(cancellationToken);
        Assert.NotNull(body?.Notes);
        Assert.Collection(body!.Notes!,
            first => Assert.Equal("newer", first.Text),
            second => Assert.Equal("older", second.Text));
    }

    [Fact]
    public async Task AddNote_returns_400_when_text_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted",
            // Match the factory's default cognito client id so the
            // cross-tenant gate (epr-0t9) doesn't pre-empt the 400 we
            // care about here.
            SubmittedBy = "test-client"
        }, cancellationToken);

        var response = await client.PostAsJsonAsync($"/work-items/{id}/notes", new { text = "   " }, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AddNote_returns_404_when_work_item_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/work-items/{Guid.NewGuid()}/notes", new { text = "anything" }, cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AddNote_returns_unauthorized_without_cognito_client_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory(includeAuthHeader: false);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/work-items/{Guid.NewGuid()}/notes", new { text = "anything" }, cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------- Task-scoped notes (RA-129 / epr-cky) ----------------------

    private static Dictionary<string, IReadOnlyCollection<WorkItemTask>> SubmittedTasks() => new()
    {
        ["submitted"] = [new WorkItemTask("check-eligibility", "Check eligibility")]
    };

    [Fact]
    public async Task AddTaskNote_persists_note_with_taskId_and_returns_updated_response()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory(
            userId: "alice-1", userName: "Alice Example",
            tasksByState: SubmittedTasks());
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        }, cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/{id}/tasks/check-eligibility/notes",
            new { text = "Reviewed evidence." },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkItemResponse>(cancellationToken);
        Assert.NotNull(body?.Notes);
        var note = Assert.Single(body!.Notes!);
        Assert.Equal("Reviewed evidence.", note.Text);
        Assert.Equal("check-eligibility", note.TaskId);
        Assert.Equal("alice-1", note.CreatedBy);

        Assert.NotNull(body.AuditLog);
        var entry = Assert.Single(body.AuditLog!, a => a.Action == "task-note-added");
        Assert.Equal("check-eligibility", entry.Details["taskId"]);
        Assert.Equal("Check eligibility", entry.Details["taskDisplayName"]);
        Assert.Equal(note.Id.ToString(), entry.Details["noteId"]);
        Assert.Equal("Reviewed evidence.", entry.Details["excerpt"]);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        var persistedNote = Assert.Single(persisted!.Notes);
        Assert.Equal("check-eligibility", persistedNote.TaskId);
    }

    [Fact]
    public async Task AddTaskNote_returns_404_when_work_item_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory(tasksByState: SubmittedTasks());
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/work-items/{Guid.NewGuid()}/tasks/check-eligibility/notes",
            new { text = "anything" },
            cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AddTaskNote_returns_problem_details_when_task_unknown()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory(tasksByState: SubmittedTasks());
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        }, cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/{id}/tasks/no-such-task/notes",
            new { text = "anything" },
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Equal("Invalid action", problem?.Title);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Empty(persisted!.Notes);
        Assert.Empty(persisted.AuditLog);
    }

    [Fact]
    public async Task AddTaskNote_returns_400_when_text_blank()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory(tasksByState: SubmittedTasks());
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        }, cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/{id}/tasks/check-eligibility/notes",
            new { text = "   " },
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Equal("Invalid request", problem?.Title);
    }

    [Fact]
    public async Task AddTaskNote_returns_400_when_body_not_json_object()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory(tasksByState: SubmittedTasks());
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        }, cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/{id}/tasks/check-eligibility/notes",
            "not-an-object",
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Equal("Invalid request", problem?.Title);
    }

    [Fact]
    public async Task AddTaskNote_returns_unauthorized_without_cognito_client_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory(includeAuthHeader: false, tasksByState: SubmittedTasks());
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/work-items/{Guid.NewGuid()}/tasks/check-eligibility/notes",
            new { text = "anything" },
            cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuditLog_is_projected_oldest_first_on_the_wire()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var older = new DateTime(2026, 4, 27, 9, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 4, 27, 11, 0, 0, DateTimeKind.Utc);
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client",
            AuditLog =
            {
                // Insert out of order on purpose so we know the oldest-first
                // ordering on the wire is enforced by the projection rather
                // than by storage order.
                new WorkItemAuditEntry
                {
                    Action = "note-added",
                    ActionDisplayName = "Note added",
                    CreatedAt = newer,
                    CreatedBy = "alice-1",
                    CreatedByName = "Alice"
                },
                new WorkItemAuditEntry
                {
                    Action = "task-completed",
                    ActionDisplayName = "Task completed",
                    Details = new() { ["taskId"] = "check-eligibility" },
                    CreatedAt = older,
                    CreatedBy = "bob-1",
                    CreatedByName = "Bob"
                }
            }
        }, cancellationToken);

        var response = await client.GetAsync($"/work-items/{id}", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<WorkItemResponse>(cancellationToken);
        Assert.NotNull(body?.AuditLog);
        Assert.Collection(body!.AuditLog!,
            first =>
            {
                Assert.Equal("task-completed", first.Action);
                Assert.Equal("Task completed", first.ActionDisplayName);
                Assert.Equal("check-eligibility", first.Details["taskId"]);
                Assert.Equal(older, first.CreatedAt);
            },
            second =>
            {
                Assert.Equal("note-added", second.Action);
                Assert.Equal(newer, second.CreatedAt);
            });
    }

    [Fact]
    public async Task AuditLog_preserves_insertion_order_for_entries_with_identical_timestamps()
    {
        // Regression for epr-s4y: when two entries share an exact CreatedAt
        // (common under FakeTimeProvider with the clock held still, and
        // possible in production when a single engine call appends two
        // entries back-to-back) the projection must keep their stored
        // insertion order rather than fall back to undefined behaviour
        // from a tied OrderBy.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var t = new DateTime(2026, 4, 30, 9, 0, 0, DateTimeKind.Utc);
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client",
            AuditLog =
            {
                new WorkItemAuditEntry
                {
                    Action = "note-added",
                    ActionDisplayName = "Note added",
                    Details = new() { ["sequence"] = "1" },
                    CreatedAt = t
                },
                new WorkItemAuditEntry
                {
                    Action = "task-completed",
                    ActionDisplayName = "Task completed",
                    Details = new() { ["sequence"] = "2" },
                    CreatedAt = t
                },
                new WorkItemAuditEntry
                {
                    Action = "action-applied",
                    ActionDisplayName = "Action applied",
                    Details = new() { ["sequence"] = "3" },
                    CreatedAt = t
                }
            }
        }, cancellationToken);

        var response = await client.GetAsync($"/work-items/{id}", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<WorkItemResponse>(cancellationToken);
        Assert.NotNull(body?.AuditLog);
        Assert.Collection(body!.AuditLog!,
            first => Assert.Equal("1", first.Details["sequence"]),
            second => Assert.Equal("2", second.Details["sequence"]),
            third => Assert.Equal("3", third.Details["sequence"]));
    }

    // ----------------------------------------------------------------
    // epr-0t9: cross-tenant IDOR protection on every mutation endpoint.
    // The mutation handlers must mirror the GetById tenancy gate and
    // return 404 when a standard caller targets an item submitted by a
    // different tenant — without ever bumping the document version.
    // Case-workers bypass the gate and reach the engine as before.
    // ----------------------------------------------------------------

    public static IEnumerable<TheoryDataRow<string, string, object?>> CrossTenantMutationCases() => new TheoryDataRow<string, string, object?>[]
    {
        new("POST", "/work-items/{id}/tasks/some-task/complete", null) { TestDisplayName = "CompleteTask" },
        new("PUT",  "/work-items/{id}/tasks/some-task/status", new { status = "Completed" }) { TestDisplayName = "SetTaskStatus" },
        new("POST", "/work-items/{id}/actions/approve", null) { TestDisplayName = "ApplyAction" },
        new("POST", "/work-items/{id}/assign", new { assigneeId = "alice-1", assigneeName = "Alice" }) { TestDisplayName = "Assign" },
        new("POST", "/work-items/{id}/unassign", null) { TestDisplayName = "Unassign" },
        new("POST", "/work-items/{id}/notes", new { text = "anything" }) { TestDisplayName = "AddNote" }
    };

    [Theory]
    [MemberData(nameof(CrossTenantMutationCases))]
    public async Task Mutation_returns_404_for_cross_tenant_caller_and_does_not_invoke_engine(
        string method, string pathTemplate, object? body)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        // Standard caller (no case-worker role); 'assign' role included so
        // the Assign / Unassign 403 path can't pre-empt the 404 we're
        // testing for.
        await using var factory = NewFactory(userRoles: "assign", userId: "actor-1");
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted",
            // Owned by someone else — caller's cognito client id is
            // 'test-client', which must NOT match.
            SubmittedBy = "other-tenant"
        }, cancellationToken);

        var path = pathTemplate.Replace("{id}", id.ToString());
        HttpResponseMessage response = method switch
        {
            "POST" when body is null => await client.PostAsync(path, content: null, cancellationToken),
            "POST" => await client.PostAsJsonAsync(path, body, cancellationToken),
            "PUT" => await client.PutAsJsonAsync(path, body!, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported method '{method}'.")
        };

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // The engine's writer would bump Version; if the gate held, the
        // document on disk is untouched.
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.NotNull(persisted);
        Assert.Equal(0, persisted!.Version);
    }

    [Theory]
    [MemberData(nameof(CrossTenantMutationCases))]
    public async Task Mutation_allows_case_worker_to_target_other_tenants_item(
        string method, string pathTemplate, object? body)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory(
            userRoles: $"{WorkItemEndpoints.CaseWorkerRole},assign",
            userId: "worker-1");
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "other-tenant"
        }, cancellationToken);

        var path = pathTemplate.Replace("{id}", id.ToString());
        HttpResponseMessage response = method switch
        {
            "POST" when body is null => await client.PostAsync(path, content: null, cancellationToken),
            "POST" => await client.PostAsJsonAsync(path, body, cancellationToken),
            "PUT" => await client.PutAsJsonAsync(path, body!, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported method '{method}'.")
        };

        // 404 means the tenancy gate fired (it must NOT for a case worker).
        // Anything else (200, 400, 409) means we got past the gate to the
        // engine — that's the contract for this test.
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Test factory backed by ephemeral MongoDB. <see cref="Persistence"/>
    /// is the real <see cref="IWorkItemPersistence"/>; tests use it to
    /// seed and re-fetch documents instead of stubbing call results.
    /// <see cref="Recording"/> exposes the last <see cref="WorkItemQuery"/>
    /// passed through, for the few tests that need to assert how the
    /// endpoint built it.
    /// </summary>
    private sealed class TestApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly MongoIntegrationFixture _fixture;
        private readonly string _databaseName = MongoIntegrationFixture.NewDatabaseName("endpoints");
        private readonly bool _includeAuthHeader;
        private readonly string? _userRoles;
        private readonly string? _userId;
        private readonly string? _userName;
        private readonly Dictionary<string, IReadOnlyCollection<WorkItemTask>>? _tasksByState;

        public TestApplicationFactory(
            MongoIntegrationFixture fixture,
            bool includeAuthHeader,
            string? userRoles,
            string? userId,
            string? userName,
            Dictionary<string, IReadOnlyCollection<WorkItemTask>>? tasksByState = null)
        {
            _fixture = fixture;
            _includeAuthHeader = includeAuthHeader;
            _userRoles = userRoles;
            _userId = userId;
            _userName = userName;
            _tasksByState = tasksByState;
        }

        public IWorkItemPersistence Persistence => Recording.Inner;

        public RecordingPersistence Recording =>
            (RecordingPersistence)Services.GetRequiredService<IWorkItemPersistence>();

        public Task SeedAsync(WorkItem item, CancellationToken cancellationToken) =>
            Persistence.CreateAsync(item, cancellationToken);

        public async Task<List<WorkItem>> AllItemsAsync(CancellationToken cancellationToken)
        {
            var page = await Persistence.QueryAsync(
                new WorkItemQuery(Page: 1, PageSize: 100), cancellationToken);
            return page.Items.ToList();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Prevent the seeder from writing seed data into the test DB.
            builder.UseSetting("WorkItems:SeedOnStartup", "false");

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IWorkItemPersistence>();
                services.RemoveAll<EprRegisterEnrolManagementBe.Utils.Mongo.IMongoDbClientFactory>();
                var clientFactory = new TestMongoDbClientFactory(
                    _fixture.ConnectionString, _databaseName);
                services.AddSingleton<EprRegisterEnrolManagementBe.Utils.Mongo.IMongoDbClientFactory>(clientFactory);
                // The real WorkItemPersistence is wrapped with a recording
                // layer so query-capture tests can observe the
                // WorkItemQuery the endpoint built. Behaviour is
                // otherwise end-to-end against ephemeral Mongo.
                services.AddSingleton<IWorkItemPersistence>(sp =>
                    new RecordingPersistence(
                        new WorkItemPersistence(
                            clientFactory,
                            sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>())));
                services.AddSingleton<IWorkItemType>(new TestWorkItemType(TypeId, "Test type", tasksByState: _tasksByState));

                // SlaBreachBackgroundService calls QueryAsync at startup which
                // sets Recording.LastQuery and contaminates tests that assert
                // the query was never issued.
                var slaBreachDescriptor = services.FirstOrDefault(
                    d => d.ImplementationType == typeof(SlaBreachBackgroundService));
                if (slaBreachDescriptor is not null)
                {
                    services.Remove(slaBreachDescriptor);
                }
            });
        }

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            if (_includeAuthHeader)
            {
                client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", "test-client");
            }
            if (_userId is not null)
            {
                client.DefaultRequestHeaders.Add("x-cdp-user-id", _userId);
            }
            if (_userName is not null)
            {
                client.DefaultRequestHeaders.Add("x-cdp-user-name", _userName);
            }
            if (_userRoles is not null)
            {
                client.DefaultRequestHeaders.Add("x-cdp-user-roles", _userRoles);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    var clientFactory = Services.GetRequiredService<EprRegisterEnrolManagementBe.Utils.Mongo.IMongoDbClientFactory>();
                    clientFactory.GetClient().DropDatabase(_databaseName);
                }
                catch
                {
                    // Best-effort cleanup; ephemeral instance dies with the
                    // fixture anyway.
                }
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Pass-through wrapper around <see cref="IWorkItemPersistence"/>
    /// that records the last <see cref="WorkItemQuery"/> the endpoint
    /// built. Behaviour is otherwise identical to the wrapped
    /// persistence — tests still go end-to-end against ephemeral Mongo.
    /// </summary>
    public sealed class RecordingPersistence(IWorkItemPersistence inner) : IWorkItemPersistence
    {
        public IWorkItemPersistence Inner { get; } = inner;
        public WorkItemQuery? LastQuery { get; private set; }

        public Task CreateAsync(WorkItem workItem, CancellationToken cancellationToken = default) =>
            Inner.CreateAsync(workItem, cancellationToken);

        public Task<bool> CreateIfAbsentAsync(WorkItem workItem, CancellationToken cancellationToken = default) =>
            Inner.CreateIfAbsentAsync(workItem, cancellationToken);

        public Task<WorkItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Inner.GetByIdAsync(id, cancellationToken);

        public Task<WorkItemPage> QueryAsync(WorkItemQuery query, CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Inner.QueryAsync(query, cancellationToken);
        }

        public Task ReplaceAsync(WorkItem workItem, CancellationToken cancellationToken = default) =>
            Inner.ReplaceAsync(workItem, cancellationToken);
    }
}
