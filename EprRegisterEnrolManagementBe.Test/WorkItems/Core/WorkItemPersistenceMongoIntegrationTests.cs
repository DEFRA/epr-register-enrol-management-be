using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// epr-bqe: real Mongo end-to-end coverage for <see cref="WorkItemPersistence"/>.
/// Uses Ephemeral MongoDB (real <c>mongod</c> on a random port, in a
/// temp data directory, torn down with the fixture) so the BSON
/// serializers registered by <see cref="MongoConventions"/> /
/// <see cref="WorkItemBsonRegistration"/>, the index definitions in
/// <see cref="WorkItemPersistence.DefineIndexes"/>, the projection that
/// strips <see cref="WorkItem.Notes"/> / <see cref="WorkItem.AuditLog"/>
/// from <see cref="WorkItemPersistence.QueryAsync"/>, and the
/// optimistic-concurrency path in <see cref="WorkItemPersistence.ReplaceAsync"/>
/// are exercised against the real driver — not faked. None of these
/// were previously covered by a Mongo-driven test (the suite mocked
/// <see cref="IWorkItemPersistence"/> wholesale), which let any
/// regression in BSON conventions, indexes or projections pass CI.
/// </summary>
public sealed class WorkItemPersistenceMongoIntegrationTests
    : IClassFixture<MongoIntegrationFixture>, IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Deterministic timestamp seed used for every <see cref="WorkItem"/>
    /// constructed in this fixture (epr-6e5). Inlined values were
    /// previously <c>DateTime.UtcNow</c>, which is wallclock-coupled
    /// and obscures which value the contract under test actually
    /// cares about.
    /// </summary>
    private static readonly DateTimeOffset s_seedNow =
        new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly MongoIntegrationFixture _fixture;
    private readonly string _databaseName;
    private readonly TestMongoDbClientFactory _clientFactory;
    private readonly WorkItemPersistence _persistence;
    private readonly FakeTimeProvider _time = new(s_seedNow);

    public WorkItemPersistenceMongoIntegrationTests(MongoIntegrationFixture fixture)
    {
        _fixture = fixture;
        // Each test class instance (xUnit creates one per [Fact]) gets a
        // fresh database name so collections / indexes from one test do
        // not leak into another.
        _databaseName = MongoIntegrationFixture.NewDatabaseName("work_items");
        _clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _persistence = new WorkItemPersistence(_clientFactory, NullLoggerFactory.Instance);
    }

    /// <summary>
    /// epr-6e5: <see cref="MongoClient"/> exposes async DB management
    /// APIs and the test runner already drives every other resource
    /// asynchronously. Prefer the async path; the synchronous
    /// <see cref="Dispose"/> remains as a safety net for the rare
    /// xUnit code path that disposes via the synchronous interface.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);
    }

    public void Dispose()
    {
        // Drop the per-test database so we don't accumulate state in the
        // pooled mongod across the test session.
        _clientFactory.GetClient().DropDatabase(_databaseName);
    }

    [Fact]
    public async Task Submit_then_get_then_query_round_trips_a_work_item_through_real_mongo()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var workItem = new WorkItem
        {
            TypeId = "re-accreditation",
            StateId = "submitted",
            SubmittedAt = now,
            LastModifiedAt = now,
            SubmittedBy = "client-1",
            CompletedTaskIdsByState =
            {
                ["submitted"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Task-One" }
            },
            TaskStatusesByState =
            {
                ["submitted"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["Task-One"] = WorkItemTaskStatus.Completed
                }
            },
            Payload = new BsonDocument { ["key"] = "value" }
        };

        await _persistence.CreateAsync(workItem, TestContext.Current.CancellationToken);

        var fetched = await _persistence.GetByIdAsync(workItem.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal("re-accreditation", fetched!.TypeId);
        Assert.Equal("submitted", fetched.StateId);
        Assert.Equal("client-1", fetched.SubmittedBy);
        Assert.Equal("value", fetched.Payload["key"].AsString);

        // Case-insensitive contract survives the Mongo round-trip on
        // both dictionaries (epr-aq5 / epr-gl6 / epr-81c).
        Assert.True(fetched.CompletedTaskIdsByState.ContainsKey("SUBMITTED"));
        Assert.Contains("task-one", fetched.CompletedTaskIdsByState["submitted"]);
        Assert.True(fetched.TaskStatusesByState.TryGetValue("SUBMITTED", out var inner));
        Assert.True(inner!.TryGetValue("TASK-ONE", out var status));
        Assert.Equal(WorkItemTaskStatus.Completed, status);

        var page = await _persistence.QueryAsync(
            new WorkItemQuery(), TestContext.Current.CancellationToken);
        Assert.Equal(1, page.TotalCount);
        Assert.Single(page.Items);
        Assert.Equal(workItem.Id, page.Items[0].Id);
    }

    [Fact]
    public async Task DefineIndexes_creates_the_seven_documented_indexes_on_startup()
    {
        // Constructor of WorkItemPersistence calls EnsureIndexes; we
        // assert what the driver actually wrote (not what we asked for)
        // so a future change to the index set is caught here.
        var collection = _clientFactory.GetCollection<WorkItem>("workItems");
        var indexes = await (await collection.Indexes.ListAsync(TestContext.Current.CancellationToken))
            .ToListAsync(TestContext.Current.CancellationToken);

        // Mongo always writes a default _id_ index in addition to ours.
        var keyDocs = indexes
            .Where(i => i["name"].AsString != "_id_")
            .Select(i => i["key"].AsBsonDocument.ToString())
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(7, keyDocs.Count);
        Assert.Contains(keyDocs, k => k.Contains("\"typeId\" : 1") && k.Contains("\"submittedAt\" : -1"));
        Assert.Contains(keyDocs, k => k.Contains("\"stateId\" : 1") && k.Contains("\"submittedAt\" : -1"));
        Assert.Contains(keyDocs, k => k.Contains("\"assignedToId\" : 1") && k.Contains("\"submittedAt\" : -1"));
        Assert.Contains(keyDocs, k =>
            k.Contains("\"submittedAt\" : -1") && !k.Contains("typeId") && !k.Contains("stateId") && !k.Contains("assignedToId"));
        // RA-125: nation + stateId compound index for fast nation-filtered worklist queries.
        Assert.Contains(keyDocs, k => k.Contains("\"payload.nation\" : 1") && k.Contains("\"stateId\" : 1"));
        // Text index for org name search — the key doc always contains "_fts":"text".
        Assert.Contains(keyDocs, k => k.Contains("\"_fts\" : \"text\""));
        // Ascending index for applicationReference prefix search.
        Assert.Contains(keyDocs, k => k.Contains("\"payload.applicationReference\" : 1"));

        // RA-219: that index must be UNIQUE (enforce one ref per work item /
        // give the engine a collision signal) and SPARSE (legacy docs without
        // the field are not indexed, so they cannot trip the constraint).
        var appRefIndex = indexes.Single(i =>
            i["key"].AsBsonDocument.Contains("payload.applicationReference"));
        Assert.True(appRefIndex.GetValue("unique", false).ToBoolean());
        Assert.True(appRefIndex.GetValue("sparse", false).ToBoolean());
    }

    [Fact]
    public async Task EnsureIndexes_reconciles_a_pre_existing_non_unique_applicationReference_index_on_startup()
    {
        // RA-219 PR review (BLOCKING): on any environment that already carries
        // the OLD non-unique payload.applicationReference index (RA-196 shipped
        // it), MongoDB raises IndexOptionsConflict (code 85) when CreateMany is
        // asked for the new unique+sparse index with the identical key. That
        // breaks service startup. This test reproduces the deploy condition and
        // proves the reconcile recovers it.
        var freshDb = MongoIntegrationFixture.NewDatabaseName("work_items_reconcile");
        var factory = new TestMongoDbClientFactory(_fixture.ConnectionString, freshDb);
        try
        {
            var collection = factory.GetCollection<WorkItem>("workItems");

            // Stand up the OLD index: same key, NON-unique, NON-sparse — the
            // exact shape that conflicts with RA-219's tightened definition.
            collection.Indexes.CreateOne(
                new CreateIndexModel<WorkItem>(
                    Builders<WorkItem>.IndexKeys.Ascending("payload.applicationReference"),
                    new CreateIndexOptions { Unique = false, Sparse = false }),
                cancellationToken: TestContext.Current.CancellationToken);

            // Constructing the persistence runs EnsureIndexes in its base ctor.
            // Before the fix this threw MongoCommandException (code 85); now it
            // must complete cleanly.
            var ex = Record.Exception(() =>
                new WorkItemPersistence(factory, NullLoggerFactory.Instance));
            Assert.Null(ex);

            // And the index must have been migrated to unique + sparse.
            var indexes = await (await collection.Indexes.ListAsync(TestContext.Current.CancellationToken))
                .ToListAsync(TestContext.Current.CancellationToken);
            var appRefIndex = indexes.Single(i =>
                i["key"].AsBsonDocument.Contains("payload.applicationReference"));
            Assert.True(appRefIndex.GetValue("unique", false).ToBoolean());
            Assert.True(appRefIndex.GetValue("sparse", false).ToBoolean());
        }
        finally
        {
            await factory.GetClient().DropDatabaseAsync(freshDb);
        }
    }

    [Fact]
    public async Task EnsureIndexes_is_idempotent_when_the_indexes_already_match()
    {
        // The reconcile must not drop/recreate when nothing changed: running
        // the persistence ctor twice against the same database is a no-op the
        // second time (CreateMany succeeds, no conflict path taken).
        var freshDb = MongoIntegrationFixture.NewDatabaseName("work_items_idempotent");
        var factory = new TestMongoDbClientFactory(_fixture.ConnectionString, freshDb);
        try
        {
            _ = new WorkItemPersistence(factory, NullLoggerFactory.Instance);
            var ex = Record.Exception(() =>
                new WorkItemPersistence(factory, NullLoggerFactory.Instance));
            Assert.Null(ex);

            var collection = factory.GetCollection<WorkItem>("workItems");
            var indexes = await (await collection.Indexes.ListAsync(TestContext.Current.CancellationToken))
                .ToListAsync(TestContext.Current.CancellationToken);
            var appRefIndex = indexes.Single(i =>
                i["key"].AsBsonDocument.Contains("payload.applicationReference"));
            Assert.True(appRefIndex.GetValue("unique", false).ToBoolean());
            Assert.True(appRefIndex.GetValue("sparse", false).ToBoolean());
        }
        finally
        {
            await factory.GetClient().DropDatabaseAsync(freshDb);
        }
    }

    [Fact]
    public async Task Unique_applicationReference_index_rejects_a_second_document_with_the_same_reference()
    {
        // RA-219: prove the unique constraint is live — a duplicate
        // payload.applicationReference is rejected with a DuplicateKey write
        // error (the very signal the engine retries on).
        var now = _time.GetUtcNow().UtcDateTime;
        WorkItem Build() => new()
        {
            TypeId = "re-accreditation",
            StateId = "submitted",
            SubmittedAt = now,
            LastModifiedAt = now,
            Payload = new MongoDB.Bson.BsonDocument { ["applicationReference"] = "RA-123456789" }
        };

        await _persistence.CreateAsync(Build(), TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<MongoDB.Driver.MongoWriteException>(() =>
            _persistence.CreateAsync(Build(), TestContext.Current.CancellationToken));
        Assert.Equal(
            MongoDB.Driver.ServerErrorCategory.DuplicateKey, ex.WriteError?.Category);
    }

    [Fact]
    public async Task Sparse_applicationReference_index_allows_multiple_documents_without_a_reference()
    {
        // RA-219: legacy / reference-less documents must coexist — the sparse
        // option means they are simply not indexed, so two of them do not
        // collide on the unique constraint.
        var now = _time.GetUtcNow().UtcDateTime;
        WorkItem Build() => new()
        {
            TypeId = "re-accreditation",
            StateId = "submitted",
            SubmittedAt = now,
            LastModifiedAt = now
        };

        await _persistence.CreateAsync(Build(), TestContext.Current.CancellationToken);
        await _persistence.CreateAsync(Build(), TestContext.Current.CancellationToken);
        // No exception: sparse index does not constrain documents missing the key.
    }

    [Fact]
    public async Task QueryAsync_excludes_notes_and_audit_log_at_the_wire_level()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var workItem = new WorkItem
        {
            TypeId = "re-accreditation",
            StateId = "submitted",
            SubmittedAt = now,
            LastModifiedAt = now,
            Notes = { new WorkItemNote { Text = "secret note body", CreatedAt = now, CreatedBy = "user-1", CreatedByName = "Alice" } },
            AuditLog = { new WorkItemAuditEntry { Action = "submitted", ActionDisplayName = "Submitted", CreatedAt = now, CreatedBy = "user-1", CreatedByName = "Alice" } }
        };
        await _persistence.CreateAsync(workItem, TestContext.Current.CancellationToken);

        // Sanity: GetByIdAsync still returns the full document.
        var full = await _persistence.GetByIdAsync(workItem.Id, TestContext.Current.CancellationToken);
        Assert.Single(full!.Notes);
        Assert.Single(full.AuditLog);

        // QueryAsync is the projected path; both collections must be empty.
        var page = await _persistence.QueryAsync(
            new WorkItemQuery(), TestContext.Current.CancellationToken);
        var item = Assert.Single(page.Items);
        Assert.Empty(item.Notes);
        Assert.Empty(item.AuditLog);
    }

    [Fact]
    public async Task ReplaceAsync_throws_concurrency_exception_when_version_does_not_match()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var workItem = new WorkItem
        {
            TypeId = "re-accreditation",
            StateId = "submitted",
            SubmittedAt = now,
            LastModifiedAt = now
        };
        await _persistence.CreateAsync(workItem, TestContext.Current.CancellationToken);

        // Simulate two readers racing: load the doc twice, persist one,
        // then try to persist the stale copy. The stale copy's version
        // matches what the document had at read time but no longer
        // matches what is on disk, so the filter should not match and
        // ReplaceAsync should throw.
        var stale = await _persistence.GetByIdAsync(workItem.Id, TestContext.Current.CancellationToken);
        var fresh = await _persistence.GetByIdAsync(workItem.Id, TestContext.Current.CancellationToken);

        fresh!.StateId = "in-progress";
        await _persistence.ReplaceAsync(fresh, TestContext.Current.CancellationToken);

        stale!.StateId = "approved";
        var ex = await Assert.ThrowsAsync<WorkItemConcurrencyException>(() =>
            _persistence.ReplaceAsync(stale, TestContext.Current.CancellationToken));
        Assert.Equal(workItem.Id, ex.WorkItemId);

        // The in-memory version on the failed attempt must roll back so
        // a caller-side retry does not double-increment.
        Assert.Equal(0, stale.Version);

        // The on-disk document is the winner's value, not the loser's.
        var final = await _persistence.GetByIdAsync(workItem.Id, TestContext.Current.CancellationToken);
        Assert.Equal("in-progress", final!.StateId);
    }

    [Fact]
    public async Task CreateIfAbsentAsync_returns_true_on_first_insert_and_false_on_duplicate_id()
    {
        // epr-33c: idempotent insert is what makes the seeder safe to
        // run from multiple instances. Two callers racing with the
        // same id must produce exactly one persisted document and one
        // false return — the existing on-disk document is left
        // untouched.
        var now = _time.GetUtcNow().UtcDateTime;
        var id = WorkItemSeed.DeterministicId("re-accreditation", "test-key");
        var first = new WorkItem
        {
            Id = id,
            TypeId = "re-accreditation",
            StateId = "submitted",
            SubmittedAt = now,
            LastModifiedAt = now,
            SubmittedBy = "first-writer"
        };
        var second = new WorkItem
        {
            Id = id,
            TypeId = "re-accreditation",
            StateId = "approved",
            SubmittedAt = now,
            LastModifiedAt = now,
            SubmittedBy = "second-writer"
        };

        var insertedFirst = await _persistence.CreateIfAbsentAsync(first, TestContext.Current.CancellationToken);
        var insertedSecond = await _persistence.CreateIfAbsentAsync(second, TestContext.Current.CancellationToken);

        Assert.True(insertedFirst);
        Assert.False(insertedSecond);

        // The first writer's document is what the database holds; the
        // second call neither overwrote nor created a duplicate.
        var fetched = await _persistence.GetByIdAsync(id, TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal("submitted", fetched!.StateId);
        Assert.Equal("first-writer", fetched.SubmittedBy);

        var page = await _persistence.QueryAsync(
            new WorkItemQuery(), TestContext.Current.CancellationToken);
        Assert.Equal(1, page.TotalCount);
    }
    // ---------------------- RA-291 SetPayloadFieldAsync ----------------------

    [Fact]
    public async Task SetPayloadFieldAsync_sets_only_the_named_field_and_leaves_the_rest_intact()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = s_seedNow.UtcDateTime;
        var workItem = new WorkItem
        {
            TypeId = "re-accreditation",
            StateId = "submitted",
            SubmittedAt = now,
            LastModifiedAt = now,
            SubmittedBy = "client-1",
        };
        workItem.ReplacePayload(new BsonDocument
        {
            ["organisationName"] = "Acme Recycling Ltd",
            ["applicationReference"] = "RA-000000123",
        });
        await _persistence.CreateAsync(workItem, ct);
        var versionBefore = workItem.Version;

        var matched = await _persistence.SetPayloadFieldAsync(
            workItem.Id, "currentQuery", new BsonDocument { ["reason"] = "why" }, ct);

        Assert.True(matched);
        var fetched = await _persistence.GetByIdAsync(workItem.Id, ct);
        Assert.Equal("why", fetched!.Payload["currentQuery"]["reason"].AsString);
        // Existing keys untouched, absent keys still absent — the guarantee
        // the whole method exists for (RA-291).
        Assert.Equal("Acme Recycling Ltd", fetched.Payload["organisationName"].AsString);
        Assert.Equal("RA-000000123", fetched.Payload["applicationReference"].AsString);
        Assert.False(fetched.Payload.Contains("accreditationId"));
        // Deliberately outside the optimistic-concurrency protocol.
        Assert.Equal(versionBefore, fetched.Version);
    }

    [Fact]
    public async Task SetPayloadFieldAsync_returns_false_when_no_work_item_matches()
    {
        var matched = await _persistence.SetPayloadFieldAsync(
            Guid.NewGuid(), "currentQuery", BsonNull.Value, TestContext.Current.CancellationToken);

        Assert.False(matched);
    }

    [Theory]
    // Dotted paths would reach into a nested document ...
    [InlineData("a.b")]
    // ... and a $-prefixed name would be read as an update operator, letting a
    // caller rewrite a different part of the envelope entirely.
    [InlineData("$set")]
    public async Task SetPayloadFieldAsync_rejects_field_names_that_escape_the_payload(
        string fieldName)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _persistence.SetPayloadFieldAsync(
                Guid.NewGuid(), fieldName, BsonNull.Value, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SetPayloadFieldAsync_rejects_a_blank_field_name(string fieldName)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _persistence.SetPayloadFieldAsync(
                Guid.NewGuid(), fieldName, BsonNull.Value, TestContext.Current.CancellationToken));
    }
}
