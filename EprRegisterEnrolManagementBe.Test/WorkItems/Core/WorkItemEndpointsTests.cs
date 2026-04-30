using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

public class WorkItemEndpointsTests
{
    private const string TypeId = "test-type";

    [Fact]
    public async Task Post_returns_unauthorized_without_cognito_client_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new TestApplicationFactory(includeAuthHeader: false);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/work-items", new { typeId = TypeId }, cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_returns_problem_when_typeId_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new TestApplicationFactory();
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
        await using var factory = new TestApplicationFactory();
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
        await using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient();

        WorkItem? captured = null;
        await factory.MockPersistence
            .CreateAsync(Arg.Do<WorkItem>(w => captured = w), Arg.Any<CancellationToken>());

        var response = await client.PostAsJsonAsync("/work-items", new
        {
            typeId = TypeId,
            payload = new { applicantName = "Acme", tonnage = 42 }
        }, cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        Assert.NotNull(captured);
        Assert.Equal(TypeId, captured!.TypeId);
        Assert.Equal("submitted", captured.StateId);
        Assert.Equal("test-client", captured.SubmittedBy);
        Assert.Equal("Acme", captured.Payload["applicantName"].AsString);
        Assert.Equal(42, captured.Payload["tonnage"].AsInt32);

        // Snapshot of the type's template (states, tasks, transitions, version)
        // is frozen onto the work item at submission for faithful historical
        // rendering after the live module changes.
        Assert.NotNull(captured.TemplateSnapshot);
        Assert.Equal("v1", captured.TemplateVersion);
        Assert.Equal("v1", captured.TemplateSnapshot!.TemplateVersion);

        Assert.NotNull(response.Headers.Location);
        Assert.StartsWith("/work-items/", response.Headers.Location!.AbsolutePath);

        var body = await response.Content.ReadFromJsonAsync<WorkItemResponse>(cancellationToken);
        Assert.NotNull(body);
        Assert.Equal(TypeId, body!.TypeId);
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
        await using var factory = new TestApplicationFactory(userId: "alice-1", userName: "Alice Example");
        using var client = factory.CreateClient();

        WorkItem? captured = null;
        await factory.MockPersistence
            .CreateAsync(Arg.Do<WorkItem>(w => captured = w), Arg.Any<CancellationToken>());

        var response = await client.PostAsJsonAsync("/work-items", new { typeId = TypeId }, cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(captured);
        var entry = Assert.Single(captured!.AuditLog);
        Assert.Equal("work-item-submitted", entry.Action);
        Assert.Equal("Work item submitted", entry.ActionDisplayName);
        Assert.Equal("alice-1", entry.CreatedBy);
        Assert.Equal("Alice Example", entry.CreatedByName);
        Assert.Equal(captured.SubmittedAt, entry.CreatedAt);
        Assert.Equal(TypeId, entry.Details["typeId"]);
        Assert.Equal("submitted", entry.Details["stateId"]);
    }

    [Fact]
    public async Task Post_returns_401_and_persists_nothing_when_user_id_claim_missing()
    {
        // RA-97 / engine rule: every mutating call requires the BFF to
        // forward a 'user:id' claim so the audit entry can be tied back
        // to a real human. Without it the engine refuses to write.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new TestApplicationFactory(userId: null);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/work-items", new { typeId = TypeId }, cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await factory.MockPersistence.DidNotReceive()
            .CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Post_accepts_request_without_payload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient();

        WorkItem? captured = null;
        await factory.MockPersistence
            .CreateAsync(Arg.Do<WorkItem>(w => captured = w), Arg.Any<CancellationToken>());

        var response = await client.PostAsJsonAsync("/work-items", new { typeId = TypeId }, cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(captured);
        Assert.Empty(captured!.Payload);
    }

    [Fact]
    public async Task Post_returns_problem_when_payload_is_not_an_object()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new TestApplicationFactory();
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
        await using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        factory.MockPersistence
            .GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(new WorkItem
            {
                Id = id,
                TypeId = TypeId,
                StateId = "submitted",
                SubmittedBy = "test-client"
            });

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
        await using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient();

        factory.MockPersistence
            .GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null);

        var response = await client.GetAsync($"/work-items/{Guid.NewGuid()}", cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_by_id_returns_not_found_for_cross_tenant_access()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        factory.MockPersistence
            .GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(new WorkItem
            {
                Id = id,
                TypeId = TypeId,
                StateId = "submitted",
                SubmittedBy = "other-tenant"
            });

        var response = await client.GetAsync($"/work-items/{id}", cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_by_id_allows_case_worker_to_read_any_tenants_item()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new TestApplicationFactory(userRoles: WorkItemEndpoints.CaseWorkerRole);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        factory.MockPersistence
            .GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(new WorkItem
            {
                Id = id,
                TypeId = TypeId,
                StateId = "submitted",
                SubmittedBy = "other-tenant"
            });

        var response = await client.GetAsync($"/work-items/{id}", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_list_filters_by_caller_client_id_when_not_case_worker()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient();

        WorkItemQuery? captured = null;
        factory.MockPersistence
            .QueryAsync(Arg.Do<WorkItemQuery>(q => captured = q), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage(Array.Empty<WorkItem>(), 0, 1, WorkItemQuery.DefaultPageSize));

        _ = await client.GetAsync("/work-items", cancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("test-client", captured!.SubmittedBy);
    }

    [Fact]
    public async Task Get_list_does_not_filter_when_case_worker()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new TestApplicationFactory(userRoles: WorkItemEndpoints.CaseWorkerRole);
        using var client = factory.CreateClient();

        WorkItemQuery? captured = null;
        factory.MockPersistence
            .QueryAsync(Arg.Do<WorkItemQuery>(q => captured = q), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage(Array.Empty<WorkItem>(), 0, 1, WorkItemQuery.DefaultPageSize));

        _ = await client.GetAsync("/work-items", cancellationToken);

        Assert.NotNull(captured);
        Assert.Null(captured!.SubmittedBy);
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

        var result = await WorkItemEndpoints.GetAll(httpContext, persistence, engine, cancellationToken);

        var ok = Assert.IsType<Ok<WorkItemListResponse>>(result.Result);
        Assert.NotNull(ok.Value);
        Assert.Empty(ok.Value!.Items);
        Assert.Equal(0, ok.Value.TotalCount);
        Assert.Equal(1, ok.Value.Page);
        Assert.Equal(WorkItemQuery.DefaultPageSize, ok.Value.PageSize);
        await persistence.DidNotReceiveWithAnyArgs().QueryAsync(default!, default);
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

        var result = await WorkItemEndpoints.GetAll(httpContext, persistence, engine, cancellationToken);

        var ok = Assert.IsType<Ok<WorkItemListResponse>>(result.Result);
        Assert.Empty(ok.Value!.Items);
        Assert.Equal(0, ok.Value.TotalCount);
        await persistence.DidNotReceiveWithAnyArgs().QueryAsync(default!, default);
    }

    [Fact]
    public async Task Get_list_rejects_page_above_cap_with_400()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/work-items?page={WorkItemQuery.MaxPage + 1}", cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // Persistence must never be hit for an out-of-range page \u2014 that's the
        // whole point of the cap.
        await factory.MockPersistence.DidNotReceiveWithAnyArgs()
            .QueryAsync(default!, default);
    }

    [Fact]
    public async Task Get_returns_paginated_envelope_for_all_persisted_work_items()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient();

        factory.MockPersistence
            .QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage(
                Items: new List<WorkItem>
                {
                    new() { TypeId = TypeId, StateId = "submitted" },
                    new() { TypeId = TypeId, StateId = "submitted" }
                },
                TotalCount: 2,
                Page: 1,
                PageSize: WorkItemQuery.DefaultPageSize));

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
        // would otherwise have them. Asserted at the JSON level so we
        // catch a regression that puts them back as `null` properties.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient();

        var assignedAt = new DateTime(2026, 4, 27, 9, 0, 0, DateTimeKind.Utc);
        // Pre-populate Notes and AuditLog as a belt-and-braces guard:
        // even if persistence projection were to leak them into the
        // returned WorkItem, the slim DTO must still strip them on the
        // way out. Assignment fields must survive so we know the
        // projection didn't strip wanted summary data.
        var workItem = new WorkItem
        {
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

        factory.MockPersistence
            .QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage(
                Items: new List<WorkItem> { workItem },
                TotalCount: 1,
                Page: 1,
                PageSize: WorkItemQuery.DefaultPageSize));

        var response = await client.GetAsync("/work-items", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Property-level assertion: the JSON must not even mention
        // 'notes' or 'auditLog' on a list item. JsonNode lets us prove
        // the absence of a property name (vs WorkItemListItemResponse
        // which physically can't carry them).
        var rawJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var root = System.Text.Json.Nodes.JsonNode.Parse(rawJson);
        Assert.NotNull(root);
        var firstItem = root!["items"]!.AsArray()[0]!.AsObject();
        Assert.False(firstItem.ContainsKey("notes"), "list item must not include 'notes'");
        Assert.False(firstItem.ContainsKey("auditLog"), "list item must not include 'auditLog'");

        // And the typed DTO surfaces the assignment summary so we know
        // the projection didn't strip anything we want.
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
        await using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient();

        WorkItemQuery? captured = null;
        factory.MockPersistence
            .QueryAsync(Arg.Do<WorkItemQuery>(q => captured = q), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage(
                Items: new List<WorkItem>(),
                TotalCount: 0,
                Page: 2,
                PageSize: 5));

        var response = await client.GetAsync(
            "/work-items?typeId=re-accreditation&typeId=other-type&stateId=submitted&search=acme&assigneeId=alice-1&unassigned=true&page=2&pageSize=5",
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

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
        await using var factory = new TestApplicationFactory(userRoles: "standard,assign", userId: "actor-1");
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var workItem = new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        };
        factory.MockPersistence.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(workItem);

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
        await factory.MockPersistence.Received(1).ReplaceAsync(workItem, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Assign_returns_400_when_assigneeId_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new TestApplicationFactory(userRoles: "standard,assign", userId: "actor-1");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/work-items/{Guid.NewGuid()}/assign", new { }, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Assign_returns_403_when_standard_user_assigns_to_someone_else()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new TestApplicationFactory(userRoles: "standard", userId: "alice-1");
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        factory.MockPersistence.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted"
        });

        var response = await client.PostAsJsonAsync(
            $"/work-items/{id}/assign", new { assigneeId = "bob-1", assigneeName = "Bob" }, cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Assign_returns_404_when_work_item_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new TestApplicationFactory(userRoles: "standard,assign", userId: "actor-1");
        using var client = factory.CreateClient();

        factory.MockPersistence.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null);

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
        await using var factory = new TestApplicationFactory(userRoles: "standard,assign", userId: "actor-1");
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var workItem = new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted",
            AssignedToId = "alice-1",
            AssignedToName = "Alice",
            AssignedAt = new DateTime(2026, 4, 27, 9, 0, 0, DateTimeKind.Utc),
            AssignedBy = "earlier-actor"
        };
        factory.MockPersistence.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(workItem);

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
        await using var factory = new TestApplicationFactory(userRoles: "standard", userId: "alice-1");
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        factory.MockPersistence.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted",
            AssignedToId = "alice-1"
        });

        var response = await client.PostAsync($"/work-items/{id}/unassign", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Assign_to_same_user_sets_idempotent_replay_header_and_does_not_persist()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new TestApplicationFactory(userRoles: "standard,assign", userId: "actor-1");
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var workItem = new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client",
            AssignedToId = "alice-1",
            AssignedToName = "Alice Example",
            AssignedAt = new DateTime(2026, 4, 27, 9, 0, 0, DateTimeKind.Utc),
            AssignedBy = "earlier-actor"
        };
        factory.MockPersistence.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(workItem);

        var response = await client.PostAsJsonAsync(
            $"/work-items/{id}/assign",
            new { assigneeId = "alice-1", assigneeName = "Alice Example" },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Idempotent-Replay", out var values));
        Assert.Equal("true", Assert.Single(values!));
        await factory.MockPersistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unassign_already_unassigned_sets_idempotent_replay_header_and_does_not_persist()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new TestApplicationFactory(userRoles: "standard,assign", userId: "actor-1");
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var workItem = new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        };
        factory.MockPersistence.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(workItem);

        var response = await client.PostAsync($"/work-items/{id}/unassign", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Idempotent-Replay", out var values));
        Assert.Equal("true", Assert.Single(values!));
        await factory.MockPersistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddNote_persists_note_and_returns_updated_response()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new TestApplicationFactory(userId: "alice-1", userName: "Alice Example");
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var workItem = new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        };
        factory.MockPersistence.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(workItem);

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

        Assert.Single(workItem.Notes);
        await factory.MockPersistence.Received(1).ReplaceAsync(workItem, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddNote_projects_notes_newest_first()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new TestApplicationFactory(userId: "alice-1");
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var older = new DateTime(2026, 4, 27, 9, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 4, 27, 11, 0, 0, DateTimeKind.Utc);
        var workItem = new WorkItem
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
        };
        factory.MockPersistence.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(workItem);

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
        await using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        factory.MockPersistence.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(new WorkItem
        {
            Id = id,
            TypeId = TypeId,
            StateId = "submitted"
        });

        var response = await client.PostAsJsonAsync($"/work-items/{id}/notes", new { text = "   " }, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AddNote_returns_404_when_work_item_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient();

        factory.MockPersistence.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null);

        var response = await client.PostAsJsonAsync(
            $"/work-items/{Guid.NewGuid()}/notes", new { text = "anything" }, cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AddNote_returns_unauthorized_without_cognito_client_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new TestApplicationFactory(includeAuthHeader: false);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/work-items/{Guid.NewGuid()}/notes", new { text = "anything" }, cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuditLog_is_projected_oldest_first_on_the_wire()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var older = new DateTime(2026, 4, 27, 9, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 4, 27, 11, 0, 0, DateTimeKind.Utc);
        var workItem = new WorkItem
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
        };
        factory.MockPersistence.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(workItem);

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
        await using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var t = new DateTime(2026, 4, 30, 9, 0, 0, DateTimeKind.Utc);
        var workItem = new WorkItem
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
        };
        factory.MockPersistence.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(workItem);

        var response = await client.GetAsync($"/work-items/{id}", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<WorkItemResponse>(cancellationToken);
        Assert.NotNull(body?.AuditLog);
        Assert.Collection(body!.AuditLog!,
            first => Assert.Equal("1", first.Details["sequence"]),
            second => Assert.Equal("2", second.Details["sequence"]),
            third => Assert.Equal("3", third.Details["sequence"]));
    }

    private sealed class TestApplicationFactory(
        bool includeAuthHeader = true,
        string? userRoles = null,
        string? userId = "test-user",
        string? userName = null) : WebApplicationFactory<Program>
    {
        public readonly IWorkItemPersistence MockPersistence = Substitute.For<IWorkItemPersistence>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IWorkItemPersistence>();
                services.AddSingleton(MockPersistence);

                // Register a known work item type for the tests.
                services.AddSingleton<IWorkItemType>(new TestWorkItemType(TypeId, "Test type"));
            });
        }

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            if (includeAuthHeader)
            {
                client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", "test-client");
            }
            if (userId is not null)
            {
                client.DefaultRequestHeaders.Add("x-cdp-user-id", userId);
            }
            if (userName is not null)
            {
                client.DefaultRequestHeaders.Add("x-cdp-user-name", userName);
            }
            if (userRoles is not null)
            {
                client.DefaultRequestHeaders.Add("x-cdp-user-roles", userRoles);
            }
        }
    }
}