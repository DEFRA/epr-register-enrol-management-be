using System.Security.Claims;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// Uses real ephemeral MongoDB so concurrency / retry behaviour is
/// exercised against the actual optimistic-concurrency implementation.
/// </summary>
public class WorkItemAuditAppenderTests
    : IAsyncDisposable
{
    private readonly TestMongoDbClientFactory _clientFactory;
    private readonly string _databaseName;
    private readonly WorkItemPersistence _persistence;

    private static readonly ClaimsPrincipal s_user = new(new ClaimsIdentity(
    [
        new Claim("user:id", "user-1"),
        new Claim("user:name", "Alice")
    ], "test"));

    public WorkItemAuditAppenderTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("audit-appender");
        _clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _persistence = new WorkItemPersistence(_clientFactory, NullLoggerFactory.Instance);
    }

    public async ValueTask DisposeAsync() =>
        await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);

    private WorkItemAuditAppender BuildSut() =>
        new(_persistence, NullLogger<WorkItemAuditAppender>.Instance);

    private async Task<WorkItem> CreateWorkItemAsync(CancellationToken ct)
    {
        var workItem = new WorkItem
        {
            TypeId = "test-type",
            StateId = "submitted",
            Payload = new BsonDocument()
        };
        await _persistence.CreateAsync(workItem, ct);
        return workItem;
    }

    [Fact]
    public async Task AppendAsync_adds_audit_entry_to_persisted_work_item()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = await CreateWorkItemAsync(ct);
        var sut = BuildSut();

        var ok = await sut.AppendAsync(
            workItem.Id,
            action: "notification-sent",
            actionDisplayName: "Email sent",
            details: new Dictionary<string, string?> { ["templateKey"] = "DulyMade", ["recipient"] = "op@ex.com" },
            s_user,
            ct);

        Assert.True(ok);

        var persisted = await _persistence.GetByIdAsync(workItem.Id, ct);
        Assert.NotNull(persisted);
        var entry = Assert.Single(persisted!.AuditLog);
        Assert.Equal("notification-sent", entry.Action);
        Assert.Equal("Email sent", entry.ActionDisplayName);
        Assert.Equal("DulyMade", entry.Details?["templateKey"]);
        Assert.Equal("user-1", entry.CreatedBy);
        Assert.Equal("Alice", entry.CreatedByName);
    }

    [Fact]
    public async Task AppendAsync_returns_false_when_work_item_not_found()
    {
        var sut = BuildSut();

        var ok = await sut.AppendAsync(
            Guid.NewGuid(),
            action: "notification-sent",
            actionDisplayName: "Email sent",
            details: new Dictionary<string, string?>(),
            s_user,
            TestContext.Current.CancellationToken);

        Assert.False(ok);
    }

    [Fact]
    public async Task AppendAsync_appends_multiple_entries_sequentially()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = await CreateWorkItemAsync(ct);
        var sut = BuildSut();

        await sut.AppendAsync(workItem.Id, "first", "First", new(), s_user, ct);
        await sut.AppendAsync(workItem.Id, "second", "Second", new(), s_user, ct);

        var persisted = await _persistence.GetByIdAsync(workItem.Id, ct);
        Assert.Equal(2, persisted!.AuditLog.Count);
        Assert.Equal("first", persisted.AuditLog[0].Action);
        Assert.Equal("second", persisted.AuditLog[1].Action);
    }

    [Fact]
    public async Task AppendAsync_retries_and_succeeds_after_one_concurrency_exception()
    {
        // Use a mock persistence so we can inject a controlled
        // WorkItemConcurrencyException on the first ReplaceAsync call
        // and verify the appender retries and ultimately succeeds.
        var ct = TestContext.Current.CancellationToken;
        var workItemId = Guid.NewGuid();
        var storedWorkItem = new WorkItem
        {
            Id = workItemId,
            TypeId = "test-type",
            StateId = "submitted",
            Payload = new BsonDocument()
        };

        var mockPersistence = Substitute.For<IWorkItemPersistence>();
        mockPersistence.GetByIdAsync(workItemId, ct).Returns(storedWorkItem);

        var replaceCallCount = 0;
        mockPersistence.ReplaceAsync(Arg.Any<WorkItem>(), ct)
            .Returns(_ =>
            {
                replaceCallCount++;
                if (replaceCallCount == 1)
                {
                    throw new WorkItemConcurrencyException(workItemId, 0);
                }

                return Task.CompletedTask;
            });

        var sut = new WorkItemAuditAppender(mockPersistence, NullLogger<WorkItemAuditAppender>.Instance);

        var ok = await sut.AppendAsync(
            workItemId,
            action: "notification-sent",
            actionDisplayName: "Email sent",
            details: new Dictionary<string, string?>(),
            s_user,
            ct);

        Assert.True(ok);
        Assert.Equal(2, replaceCallCount);
        await mockPersistence.Received(2).GetByIdAsync(workItemId, ct);
    }
}
