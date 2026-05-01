using System.Security.Claims;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// Regression coverage for epr-bwk: every persisted timestamp on
/// <see cref="WorkItem"/>, <see cref="WorkItemNote"/> and
/// <see cref="WorkItemAuditEntry"/> must come from the injected
/// <see cref="TimeProvider"/>. The model types must NOT default their
/// timestamp properties to a wallclock value, otherwise tests that wire
/// up a <see cref="FakeTimeProvider"/> can be silently undermined when
/// the engine reads a model field it forgot to assign.
///
/// epr-efp: backed by ephemeral MongoDB so the assertions reflect what
/// the real driver actually persisted, not what the in-memory mock
/// captured. The previous implementation substituted
/// <see cref="IWorkItemPersistence"/> wholesale.
/// </summary>
public class WorkItemServiceTimestampTests
    : IClassFixture<MongoIntegrationFixture>, IAsyncDisposable
{
    private const string TypeId = "test-type";
    private static readonly DateTimeOffset T = new(2026, 4, 27, 10, 0, 0, TimeSpan.Zero);

    private readonly TestMongoDbClientFactory _clientFactory;
    private readonly string _databaseName;
    private readonly WorkItemPersistence _persistence;
    private readonly FakeTimeProvider _time = new(T);

    public WorkItemServiceTimestampTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("svc_timestamps");
        _clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _persistence = new WorkItemPersistence(_clientFactory, NullLoggerFactory.Instance);
    }

    public async ValueTask DisposeAsync() =>
        await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);

    private WorkItemService BuildService(IWorkItemType type) =>
        new(
            new WorkItemRegistry([type]),
            _persistence,
            NullLogger<WorkItemService>.Instance,
            _time);

    private static TestWorkItemType BuildType() =>
        new(
            TypeId,
            "Test type",
            initialState: new WorkItemState("submitted", "Submitted"),
            states: [new WorkItemState("submitted", "Submitted")],
            tasksByState: new Dictionary<string, IReadOnlyCollection<WorkItemTask>>
            {
                ["submitted"] = [new WorkItemTask("check-eligibility", "Check eligibility")]
            });

    private static ClaimsPrincipal User() =>
        new(new ClaimsIdentity(
        [
            new Claim("cognito:client_id", "test-client"),
            new Claim("user:id", "alice-1"),
            new Claim("user:name", "Alice Example")
        ], "test"));

    private async Task<WorkItem> SeedAsync(WorkItem workItem)
    {
        await _persistence.CreateAsync(workItem, TestContext.Current.CancellationToken);
        return workItem;
    }

    private async Task<WorkItem> GetAsync(Guid id)
    {
        var fetched = await _persistence.GetByIdAsync(id, TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        return fetched!;
    }

    [Fact]
    public async Task Submit_stamps_SubmittedAt_LastModifiedAt_and_birth_audit_from_TimeProvider()
    {
        var result = await BuildService(BuildType()).SubmitAsync(
            BuildType(), new BsonDocument(), submittedBy: "test-client",
            User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Message);

        var fetched = await GetAsync(result.WorkItem!.Id);
        var expected = T.UtcDateTime;
        Assert.Equal(expected, fetched.SubmittedAt);
        Assert.Equal(expected, fetched.LastModifiedAt);
        var birth = Assert.Single(fetched.AuditLog);
        Assert.Equal("work-item-submitted", birth.Action);
        Assert.Equal(expected, birth.CreatedAt);
    }

    [Fact]
    public async Task AddNote_records_CreatedAt_and_LastModifiedAt_from_advanced_TimeProvider()
    {
        var workItem = await SeedAsync(new WorkItem
        {
            Id = Guid.NewGuid(),
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedAt = T.UtcDateTime,
            LastModifiedAt = T.UtcDateTime,
            SubmittedBy = "test-client"
        });

        _time.Advance(TimeSpan.FromMinutes(1));
        var expected = _time.GetUtcNow().UtcDateTime;

        var result = await BuildService(BuildType()).AddNoteAsync(
            workItem.Id, "Reviewed evidence.", User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Message);

        var fetched = await GetAsync(workItem.Id);
        var note = Assert.Single(fetched.Notes);
        Assert.Equal(expected, note.CreatedAt);
        Assert.Equal(expected, fetched.LastModifiedAt);
        var audit = Assert.Single(fetched.AuditLog);
        Assert.Equal("note-added", audit.Action);
        Assert.Equal(expected, audit.CreatedAt);
    }

    [Fact]
    public async Task CompleteTask_records_LastModifiedAt_and_audit_timestamp_from_advanced_TimeProvider()
    {
        var workItem = await SeedAsync(new WorkItem
        {
            Id = Guid.NewGuid(),
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedAt = T.UtcDateTime,
            LastModifiedAt = T.UtcDateTime,
            SubmittedBy = "test-client"
        });

        _time.Advance(TimeSpan.FromMinutes(2));
        var expected = _time.GetUtcNow().UtcDateTime;

        var result = await BuildService(BuildType()).CompleteTaskAsync(
            workItem.Id, "check-eligibility", User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Message);

        var fetched = await GetAsync(workItem.Id);
        Assert.Equal(expected, fetched.LastModifiedAt);
        var audit = Assert.Single(fetched.AuditLog);
        Assert.Equal("task-completed", audit.Action);
        Assert.Equal(expected, audit.CreatedAt);
    }

    [Fact]
    public void Default_construction_leaves_timestamps_at_MinValue_so_engine_bugs_are_loud()
    {
        var workItem = new WorkItem { TypeId = TypeId, StateId = "submitted" };
        Assert.Equal(default, workItem.SubmittedAt);
        Assert.Equal(default, workItem.LastModifiedAt);

        var note = new WorkItemNote { Text = "x" };
        Assert.Equal(default, note.CreatedAt);

        var entry = new WorkItemAuditEntry { Action = "x", ActionDisplayName = "X" };
        Assert.Equal(default, entry.CreatedAt);
    }
}
