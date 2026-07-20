using System.Net;
using MongoDB.Bson;
using System.Net.Http.Json;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EprRegisterEnrolManagementBe.Test;

/// <summary>
/// epr-efp: this suite exists to prove the global exception handler
/// turns unhandled exceptions into RFC 7807 <see cref="ProblemDetails"/>
/// — a middleware contract, not a persistence contract. To force the
/// exception we wrap a real <see cref="WorkItemPersistence"/>
/// (ephemeral Mongo) in a thin fault-injection decorator so the test
/// still exercises the production HTTP pipeline end-to-end without
/// substituting the persistence layer wholesale (the previous
/// implementation mocked <see cref="IWorkItemPersistence"/>).
/// </summary>
public class ProblemDetailsExceptionHandlerTests
    : IClassFixture<MongoIntegrationFixture>
{
    private readonly MongoIntegrationFixture _fixture;

    public ProblemDetailsExceptionHandlerTests(MongoIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Unhandled_exception_is_returned_as_problem_details_with_500()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new ThrowingFactory(_fixture);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", "test-client");

        var response = await client.GetAsync("/work-items", ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(ct);
        Assert.NotNull(problem);
        Assert.Equal(StatusCodes.Status500InternalServerError, problem!.Status);
    }

    private sealed class ThrowingFactory(MongoIntegrationFixture fixture) : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = MongoIntegrationFixture.NewDatabaseName("problem_details");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.UseEphemeralMongoPersistence(fixture, _databaseName);

                // Wrap the real persistence so QueryAsync throws — the
                // rest of the service surface is the production code
                // path.
                services.RemoveAll<IWorkItemPersistence>();
                var clientFactory = new TestMongoDbClientFactory(
                    fixture.ConnectionString, _databaseName);
                var real = new WorkItemPersistence(clientFactory, NullLoggerFactory.Instance);
                services.AddSingleton<IWorkItemPersistence>(new ThrowingOnQueryPersistence(real));
            });
        }
    }

    /// <summary>
    /// Wraps a real <see cref="IWorkItemPersistence"/> and throws on
    /// <see cref="IWorkItemPersistence.QueryAsync"/> only. Every other
    /// member delegates so the fault is surgical: the exception handler
    /// is the only thing under test.
    /// </summary>
    private sealed class ThrowingOnQueryPersistence(IWorkItemPersistence inner) : IWorkItemPersistence
    {
        public Task<bool> SetPayloadFieldAsync(
            Guid workItemId,
            string fieldName,
            BsonValue value,
            CancellationToken cancellationToken = default) =>
            inner.SetPayloadFieldAsync(workItemId, fieldName, value, cancellationToken);

        public Task CreateAsync(WorkItem workItem, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(workItem, cancellationToken);

        public Task<bool> CreateIfAbsentAsync(WorkItem workItem, CancellationToken cancellationToken = default) =>
            inner.CreateIfAbsentAsync(workItem, cancellationToken);

        public Task<WorkItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.GetByIdAsync(id, cancellationToken);

        public Task<WorkItemPage> QueryAsync(WorkItemQuery query, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");

        public Task ReplaceAsync(WorkItem workItem, CancellationToken cancellationToken = default) =>
            inner.ReplaceAsync(workItem, cancellationToken);
    }
}
