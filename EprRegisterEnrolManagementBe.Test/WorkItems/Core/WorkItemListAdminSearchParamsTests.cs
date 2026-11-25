using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolManagementBe.Auth;
using EprRegisterEnrolManagementBe.Test.Auth;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-751: the <c>q</c>, <c>assigned</c> and <c>archived</c> parameters of
/// <c>GET /work-items</c>. <c>assigned</c> and <c>archived</c> are optional,
/// but when sent they must be exactly <c>true</c> or <c>false</c>: anything
/// else is a 400 ValidationProblem and an Error log, never a silently dropped
/// filter.
/// </summary>
public class WorkItemListAdminSearchParamsTests
{
    private static QueryCollection Query(params (string Key, string[] Values)[] pairs) =>
        new(pairs.ToDictionary(p => p.Key, p => new StringValues(p.Values)));

    [Fact]
    public void Binds_q_assigned_and_archived()
    {
        var query = WorkItemQueryBinding.FromQueryString(
            Query(("q", ["  RA-900000751 "]), ("assigned", ["TRUE"]), ("archived", ["false"]))
        );

        Assert.Equal("RA-900000751", query.NormalisedQ);
        Assert.True(query.Assigned);
        Assert.False(query.Archived);
    }

    [Fact]
    public void Absent_params_bind_to_no_filter()
    {
        var query = WorkItemQueryBinding.FromQueryString(Query());

        Assert.Null(query.NormalisedQ);
        Assert.Null(query.Assigned);
        Assert.Null(query.Archived);
        Assert.Empty(WorkItemQueryBinding.Validate(Query()));
    }

    [Fact]
    public void Blank_q_is_no_filter()
    {
        Assert.Null(WorkItemQueryBinding.FromQueryString(Query(("q", ["   "]))).NormalisedQ);
    }

    [Theory]
    [InlineData("assigned", "yes")]
    [InlineData("assigned", "1")]
    [InlineData("assigned", "")]
    [InlineData("archived", "on")]
    [InlineData("archived", "maybe")]
    [InlineData("archived", "")]
    public void Validate_rejects_anything_but_true_or_false(string key, string value)
    {
        var errors = WorkItemQueryBinding.Validate(Query((key, [value])));

        Assert.Equal([$"'{key}' must be 'true' or 'false'."], errors[key]);
    }

    [Fact]
    public void Validate_rejects_a_repeated_assigned()
    {
        var errors = WorkItemQueryBinding.Validate(Query(("assigned", ["true", "false"])));

        Assert.True(errors.ContainsKey("assigned"));
    }

    [Theory]
    [InlineData("unassigned", "true")]
    [InlineData("assigneeId", "alice")]
    public void Validate_rejects_assigned_combined_with_the_older_assignee_filters(string key, string value)
    {
        var errors = WorkItemQueryBinding.Validate(Query(("assigned", ["true"]), (key, [value])));

        Assert.Equal(
            ["'assigned' cannot be combined with 'unassigned' or 'assigneeId'."],
            errors["assigned"]
        );
    }

    private static (BareFactory Factory, WebApplicationFactory<Program> App, CapturingLoggerProvider Logs) NewApp()
    {
        var factory = new BareFactory(environment: "Development");
        factory
            .MockPersistence.QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage(Array.Empty<WorkItem>(), 0, 1, 50));
        var logs = new CapturingLoggerProvider();
        var app = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILoggerFactory>();
                services.AddSingleton<ILoggerFactory>(_ =>
                    LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs))
                );
            })
        );
        return (factory, app, logs);
    }

    private static HttpClient Client(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, CallerAccessPolicies.AdminUiClientIdDefault);
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultUserIdHeaderName, "entra-oid-123");
        return client;
    }

    [Theory]
    [InlineData("/work-items?assigned=maybe", "assigned")]
    [InlineData("/work-items?archived=", "archived")]
    [InlineData("/work-items?assigned=true&unassigned=true", "assigned")]
    public async Task Invalid_params_answer_400_validation_problem_and_log_at_error(string url, string field)
    {
        var (factory, app, logs) = NewApp();
        await using var _f = factory;
        await using var _a = app;
        using var client = Client(app);

        var response = await client.GetAsync(url, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(
            TestContext.Current.CancellationToken
        );
        Assert.Equal("Invalid query", problem!.Title);
        Assert.True(problem.Errors.ContainsKey(field));
        var log = Assert.Single(logs.Records, r => r.Message == "Work item list query rejected");
        Assert.Equal(LogLevel.Error, log.Level);
        await factory
            .MockPersistence.DidNotReceive()
            .QueryAsync(Arg.Is<WorkItemQuery>(q => q.PageSize == 50), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Valid_params_reach_persistence()
    {
        var (factory, app, _) = NewApp();
        await using var _f = factory;
        await using var _a = app;
        using var client = Client(app);

        var response = await client.GetAsync(
            "/work-items?q=RA-9&assigned=false&archived=true&pageSize=50",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await factory
            .MockPersistence.Received(1)
            .QueryAsync(
                Arg.Is<WorkItemQuery>(q =>
                    q.Q == "RA-9" && q.Assigned == false && q.Archived == true && q.PageSize == 50
                ),
                Arg.Any<CancellationToken>()
            );
    }
}
