using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

public class WorkItemEngineEndpointsTests
{
    private const string TypeId = "test-type";

    [Fact]
    public async Task Complete_task_returns_updated_work_item_with_task_marked_complete()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new EngineFactory();
        using var client = factory.CreateClient();

        var workItemId = Guid.NewGuid();
        var stored = new WorkItem
        {
            Id = workItemId,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        };
        factory.MockPersistence.GetByIdAsync(workItemId, Arg.Any<CancellationToken>()).Returns(stored);

        var response = await client.PostAsync(
            $"/work-items/{workItemId}/tasks/check-eligibility/complete", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkItemResponse>(cancellationToken);
        Assert.NotNull(body);
        Assert.Single(body!.Tasks);
        Assert.True(body.Tasks.Single().IsComplete);
        await factory.MockPersistence.Received(1).ReplaceAsync(stored, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Action_returns_409_when_tasks_outstanding()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new EngineFactory();
        using var client = factory.CreateClient();

        var workItemId = Guid.NewGuid();
        factory.MockPersistence.GetByIdAsync(workItemId, Arg.Any<CancellationToken>()).Returns(new WorkItem
        {
            Id = workItemId,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        });

        var response = await client.PostAsync(
            $"/work-items/{workItemId}/actions/approve", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Equal("Action not allowed", problem?.Title);
    }

    [Fact]
    public async Task Action_transitions_state_when_tasks_complete()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new EngineFactory();
        using var client = factory.CreateClient();

        var workItemId = Guid.NewGuid();
        factory.MockPersistence.GetByIdAsync(workItemId, Arg.Any<CancellationToken>()).Returns(new WorkItem
        {
            Id = workItemId,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client",
            CompletedTaskIdsByState = new() { ["submitted"] = ["check-eligibility"] }
        });

        var response = await client.PostAsync(
            $"/work-items/{workItemId}/actions/approve", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkItemResponse>(cancellationToken);
        Assert.Equal("approved", body?.StateId);
        Assert.Empty(body!.AvailableActions);
    }

    [Fact]
    public async Task Action_returns_404_when_work_item_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new EngineFactory();
        using var client = factory.CreateClient();

        factory.MockPersistence.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var response = await client.PostAsync(
            $"/work-items/{Guid.NewGuid()}/actions/approve", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Action_returns_400_when_action_unknown()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new EngineFactory();
        using var client = factory.CreateClient();

        var workItemId = Guid.NewGuid();
        factory.MockPersistence.GetByIdAsync(workItemId, Arg.Any<CancellationToken>()).Returns(new WorkItem
        {
            Id = workItemId,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        });

        var response = await client.PostAsync(
            $"/work-items/{workItemId}/actions/teleport", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_by_id_projects_engine_state_in_response()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new EngineFactory();
        using var client = factory.CreateClient();

        var workItemId = Guid.NewGuid();
        factory.MockPersistence.GetByIdAsync(workItemId, Arg.Any<CancellationToken>()).Returns(new WorkItem
        {
            Id = workItemId,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        });

        var body = await client.GetFromJsonAsync<WorkItemResponse>($"/work-items/{workItemId}", cancellationToken);

        Assert.NotNull(body);
        Assert.Single(body!.Tasks);
        Assert.False(body.Tasks.Single().IsComplete);
        Assert.Empty(body.AvailableActions); // approve is gated on the task
    }

    // ---------------------- Task status endpoint (epr-gl6) ----------------------

    [Fact]
    public async Task Set_task_status_returns_updated_response_with_in_progress_status()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new EngineFactory();
        using var client = factory.CreateClient();

        var workItemId = Guid.NewGuid();
        factory.MockPersistence.GetByIdAsync(workItemId, Arg.Any<CancellationToken>()).Returns(new WorkItem
        {
            Id = workItemId,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        });

        var response = await client.PutAsJsonAsync(
            $"/work-items/{workItemId}/tasks/check-eligibility/status",
            new { status = "InProgress" }, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkItemResponse>(cancellationToken);
        var task = Assert.Single(body!.Tasks);
        Assert.Equal(WorkItemTaskStatus.InProgress, task.Status);
        Assert.False(task.IsComplete);
    }

    [Theory]
    [InlineData("in-progress")]
    [InlineData("nonsense")]
    [InlineData("")]
    public async Task Set_task_status_returns_400_for_invalid_status_value(string statusValue)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new EngineFactory();
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(
            $"/work-items/{Guid.NewGuid()}/tasks/check-eligibility/status",
            new { status = statusValue }, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Set_task_status_returns_401_when_user_id_header_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new EngineFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Remove("x-cdp-user-id");

        var response = await client.PutAsJsonAsync(
            $"/work-items/{Guid.NewGuid()}/tasks/check-eligibility/status",
            new { status = "InProgress" }, cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed class EngineFactory : WebApplicationFactory<Program>
    {
        public readonly IWorkItemPersistence MockPersistence = Substitute.For<IWorkItemPersistence>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IWorkItemPersistence>();
                services.AddSingleton(MockPersistence);

                services.AddSingleton<IWorkItemType>(new TestWorkItemType(
                    TypeId,
                    "Test type",
                    states:
                    [
                        new WorkItemState("submitted", "Submitted"),
                        new WorkItemState("approved", "Approved", IsTerminal: true)
                    ],
                    tasksByState: new Dictionary<string, IReadOnlyCollection<WorkItemTask>>
                    {
                        ["submitted"] = [new WorkItemTask("check-eligibility", "Check eligibility")]
                    },
                    transitions:
                    [
                        new WorkItemTransition("approve", "Approve", "submitted", "approved")
                    ]));
            });
        }

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", "test-client");
            client.DefaultRequestHeaders.Add("x-cdp-user-id", "test-user");
        }
    }
}