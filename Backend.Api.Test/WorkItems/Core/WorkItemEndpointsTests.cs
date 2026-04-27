using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Backend.Api.WorkItems.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Backend.Api.Test.WorkItems.Core;

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
            "/work-items?typeId=re-accreditation&typeId=other-type&stateId=submitted&search=acme&page=2&pageSize=5",
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.NotNull(captured);
        Assert.NotNull(captured!.TypeIds);
        Assert.Equal(new[] { "re-accreditation", "other-type" }, captured.TypeIds);
        Assert.Equal(new[] { "submitted" }, captured.StateIds);
        Assert.Equal("acme", captured.Search);
        Assert.Equal(2, captured.Page);
        Assert.Equal(5, captured.PageSize);
    }

    private sealed class TestApplicationFactory(bool includeAuthHeader = true) : WebApplicationFactory<Program>
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
        }
    }
}
