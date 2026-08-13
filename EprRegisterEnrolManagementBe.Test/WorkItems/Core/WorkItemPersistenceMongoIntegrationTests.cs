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
/// serializers registered by <see cref="MongoConventions"/>, the index definitions in
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
    : IAsyncDisposable, IDisposable
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
            Payload = new BsonDocument { ["key"] = "value" }
        };

        await _persistence.CreateAsync(workItem, TestContext.Current.CancellationToken);

        var fetched = await _persistence.GetByIdAsync(workItem.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal("re-accreditation", fetched!.TypeId);
        Assert.Equal("submitted", fetched.StateId);
        Assert.Equal("client-1", fetched.SubmittedBy);
        Assert.Equal("value", fetched.Payload["key"].AsString);

        var page = await _persistence.QueryAsync(
            new WorkItemQuery(), TestContext.Current.CancellationToken);
        Assert.Equal(1, page.TotalCount);
        Assert.Single(page.Items);
        Assert.Equal(workItem.Id, page.Items[0].Id);
    }

    [Fact]
    public async Task DefineIndexes_creates_the_nine_documented_indexes_on_startup()
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

        Assert.Equal(9, keyDocs.Count);
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
        // RA-311/MBE-3: ascending index backing the operatorApplicationId
        // idempotent-submit lookup.
        Assert.Contains(keyDocs, k => k.Contains("\"payload.operatorApplicationId\" : 1"));
        // epr-r9oy: ascending index backing AccreditationIdLookup.ExistsAsync.
        Assert.Contains(keyDocs, k => k.Contains("\"payload.accreditationId\" : 1"));

        // RA-219: that index must be UNIQUE (enforce one ref per work item /
        // give the engine a collision signal) and SPARSE (legacy docs without
        // the field are not indexed, so they cannot trip the constraint).
        var appRefIndex = indexes.Single(i =>
            i["key"].AsBsonDocument.Contains("payload.applicationReference"));
        Assert.True(appRefIndex.GetValue("unique", false).ToBoolean());
        Assert.True(appRefIndex.GetValue("sparse", false).ToBoolean());

        // RA-311/MBE-3: same UNIQUE + SPARSE contract for operatorApplicationId
        // — one work item per operator application id, but legacy/manual
        // items without the field are never constrained.
        var operatorApplicationIdIndex = indexes.Single(i =>
            i["key"].AsBsonDocument.Contains("payload.operatorApplicationId"));
        Assert.True(operatorApplicationIdIndex.GetValue("unique", false).ToBoolean());
        Assert.True(operatorApplicationIdIndex.GetValue("sparse", false).ToBoolean());

        // epr-r9oy: UNIQUE but PARTIAL rather than sparse. Sparse would exclude
        // only documents where the field is absent, and duly making writes an
        // explicit null — under sparse the second such write in the collection
        // fails with E11000 and the regulator gets a 500. Asserting NOT sparse
        // is the half that stops a well-meaning "make it consistent with the
        // two above" from reintroducing the outage.
        var accreditationIdIndex = indexes.Single(i =>
            i["key"].AsBsonDocument.Contains("payload.accreditationId"));
        Assert.True(accreditationIdIndex.GetValue("unique", false).ToBoolean());
        Assert.False(accreditationIdIndex.GetValue("sparse", false).ToBoolean());
        Assert.True(accreditationIdIndex.Contains("partialFilterExpression"));
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

    // ─────────────────────── RA-324 filter + sort (real Mongo) ───────────────────────

    private async Task SeedAsync(
        string organisationName,
        string? operatorOrganisationId = null,
        string? material = null,
        string stateId = "submitted",
        int submittedMinutesAgo = 0,
        WorkItemSlaClock? slaClock = null)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var payload = new BsonDocument { ["organisationName"] = organisationName };
        if (operatorOrganisationId is not null)
        {
            payload["operatorOrganisationId"] = operatorOrganisationId;
        }
        if (material is not null)
        {
            payload["material"] = material;
        }
        var item = new WorkItem
        {
            TypeId = "re-accreditation",
            StateId = stateId,
            SubmittedAt = now.AddMinutes(-submittedMinutesAgo),
            LastModifiedAt = now,
            Payload = payload,
            SlaClock = slaClock
        };
        await _persistence.CreateAsync(item, TestContext.Current.CancellationToken);
    }

    private static string[] OrgOrder(WorkItemPage page) =>
        page.Items.Select(i => i.Payload["organisationName"].AsString).ToArray();

    [Fact]
    public async Task QueryAsync_default_sort_is_newest_submitted_first()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync("older", submittedMinutesAgo: 60);
        await SeedAsync("newer", submittedMinutesAgo: 0);

        var page = await _persistence.QueryAsync(new WorkItemQuery(), ct);

        Assert.Equal(new[] { "newer", "older" }, OrgOrder(page));
    }

    [Fact]
    public async Task QueryAsync_sort_organisation_orders_case_insensitively_ascending()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync("banana");
        await SeedAsync("Apple");
        await SeedAsync("Cherry");

        var page = await _persistence.QueryAsync(new WorkItemQuery(Sort: "organisation"), ct);

        Assert.Equal(new[] { "Apple", "banana", "Cherry" }, OrgOrder(page));
    }

    [Fact]
    public async Task QueryAsync_sort_organisation_descending_reverses()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync("banana");
        await SeedAsync("Apple");
        await SeedAsync("Cherry");

        var page = await _persistence.QueryAsync(
            new WorkItemQuery(Sort: "organisation", SortDescending: true), ct);

        Assert.Equal(new[] { "Cherry", "banana", "Apple" }, OrgOrder(page));
    }

    [Fact]
    public async Task QueryAsync_sort_status_orders_by_workflow_rank()
    {
        var ct = TestContext.Current.CancellationToken;
        // Seeded out of workflow order. All non-terminal, but since RA-313 that
        // is incidental — a bare query no longer filters terminal states out.
        await SeedAsync("q", stateId: "queried");
        await SeedAsync("aw", stateId: "awaiting-decision");
        await SeedAsync("s", stateId: "submitted");
        await SeedAsync("a", stateId: "assessment-in-progress");
        await SeedAsync("d", stateId: "duly-made");

        var page = await _persistence.QueryAsync(new WorkItemQuery(Sort: "status"), ct);

        Assert.Equal(
            new[] { "submitted", "duly-made", "assessment-in-progress", "awaiting-decision", "queried" },
            page.Items.Select(i => i.StateId).ToArray());
    }

    [Fact]
    public async Task QueryAsync_sort_status_descending_reverses_rank_with_submitted_tiebreak()
    {
        var ct = TestContext.Current.CancellationToken;
        // Two items share the 'submitted' rank so the submittedAt-desc tiebreak
        // is exercised end-to-end, not just at the BSON level.
        await SeedAsync("q", stateId: "queried");
        await SeedAsync("a", stateId: "assessment-in-progress");
        await SeedAsync("s-old", stateId: "submitted", submittedMinutesAgo: 60);
        await SeedAsync("s-new", stateId: "submitted", submittedMinutesAgo: 0);

        var page = await _persistence.QueryAsync(
            new WorkItemQuery(Sort: "status", SortDescending: true), ct);

        // Descending workflow rank: queried(4), assessment-in-progress(2), then
        // the two submitted(0) items broken newest-first by the tiebreak.
        Assert.Equal(new[] { "q", "a", "s-new", "s-old" }, OrgOrder(page));
    }

    [Fact]
    public async Task QueryAsync_sorted_path_paginates_and_reports_full_total()
    {
        var ct = TestContext.Current.CancellationToken;
        // Five items, page size 2, sorted A→Z: exercises the aggregation
        // $sort → $unset → $skip → $limit across more than one page and checks
        // TotalCount reflects the full filtered count, not the page size — so a
        // swapped $sort/$limit order or off-by-one $skip cannot stay green.
        foreach (var name in new[] { "e", "a", "d", "b", "c" })
        {
            await SeedAsync(name);
        }

        var page1 = await _persistence.QueryAsync(
            new WorkItemQuery(Sort: "organisation", Page: 1, PageSize: 2), ct);
        var page2 = await _persistence.QueryAsync(
            new WorkItemQuery(Sort: "organisation", Page: 2, PageSize: 2), ct);
        var page3 = await _persistence.QueryAsync(
            new WorkItemQuery(Sort: "organisation", Page: 3, PageSize: 2), ct);

        Assert.Equal(new[] { "a", "b" }, OrgOrder(page1));
        Assert.Equal(new[] { "c", "d" }, OrgOrder(page2));
        Assert.Equal(new[] { "e" }, OrgOrder(page3));
        Assert.Equal(5, page1.TotalCount);
        Assert.Equal(5, page2.TotalCount);
        Assert.Equal(2, page1.PageSize);
        Assert.Equal(2, page2.Page);
    }

    [Fact]
    public async Task QueryAsync_sort_due_date_soonest_first_and_no_clock_last()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = _time.GetUtcNow().UtcDateTime;
        // Same start, longer target => later deadline: proves the sort reflects
        // targetDuration (SLA extensions), not just startedAt.
        await SeedAsync("B", stateId: "assessment-in-progress",
            slaClock: new WorkItemSlaClock { StartedAt = now, TargetDuration = TimeSpan.FromDays(30) });
        await SeedAsync("A", stateId: "assessment-in-progress",
            slaClock: new WorkItemSlaClock { StartedAt = now, TargetDuration = TimeSpan.FromDays(10) });
        await SeedAsync("NoClock", stateId: "submitted", slaClock: null);
        await SeedAsync("C", stateId: "assessment-in-progress",
            slaClock: new WorkItemSlaClock { StartedAt = now.AddDays(-5), TargetDuration = TimeSpan.FromDays(84) });

        var page = await _persistence.QueryAsync(new WorkItemQuery(Sort: "due-date"), ct);

        // A (now+10), B (now+30), C (now+79), then the clock-less item last.
        Assert.Equal(new[] { "A", "B", "C", "NoClock" }, OrgOrder(page));
    }

    [Fact]
    public async Task QueryAsync_sort_due_date_descending_keeps_no_clock_item_last()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = _time.GetUtcNow().UtcDateTime;
        await SeedAsync("B", stateId: "assessment-in-progress",
            slaClock: new WorkItemSlaClock { StartedAt = now, TargetDuration = TimeSpan.FromDays(30) });
        await SeedAsync("A", stateId: "assessment-in-progress",
            slaClock: new WorkItemSlaClock { StartedAt = now, TargetDuration = TimeSpan.FromDays(10) });
        await SeedAsync("NoClock", stateId: "submitted", slaClock: null);

        var page = await _persistence.QueryAsync(
            new WorkItemQuery(Sort: "due-date", SortDescending: true), ct);

        // Latest deadline first, but the clock-less item still sorts last.
        Assert.Equal(new[] { "B", "A", "NoClock" }, OrgOrder(page));
    }

    [Fact]
    public async Task QueryAsync_sorted_path_still_strips_notes_and_audit_log()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = _time.GetUtcNow().UtcDateTime;
        var item = new WorkItem
        {
            TypeId = "re-accreditation",
            StateId = "submitted",
            SubmittedAt = now,
            LastModifiedAt = now,
            Payload = new BsonDocument { ["organisationName"] = "Acme" },
            Notes = { new WorkItemNote { Text = "secret", CreatedAt = now, CreatedBy = "u", CreatedByName = "U" } },
            AuditLog = { new WorkItemAuditEntry { Action = "submitted", ActionDisplayName = "Submitted", CreatedAt = now, CreatedBy = "u", CreatedByName = "U" } }
        };
        await _persistence.CreateAsync(item, ct);

        var page = await _persistence.QueryAsync(new WorkItemQuery(Sort: "organisation"), ct);

        var got = Assert.Single(page.Items);
        // Projection preserved on the aggregation path, and the computed sort
        // fields were $unset so the document still deserialises cleanly.
        Assert.Empty(got.Notes);
        Assert.Empty(got.AuditLog);
        Assert.Equal("Acme", got.Payload["organisationName"].AsString);
    }

    [Fact]
    public async Task QueryAsync_material_filter_matches_case_insensitively()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync("p", material: "plastic");
        await SeedAsync("g", material: "glass");

        var page = await _persistence.QueryAsync(
            new WorkItemQuery(Materials: new[] { "PLASTIC" }), ct);

        var got = Assert.Single(page.Items);
        Assert.Equal("plastic", got.Payload["material"].AsString);
    }

    [Fact]
    public async Task QueryAsync_material_filter_matches_any_of_multiple_selections()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync("p", material: "plastic");
        await SeedAsync("g", material: "glass");
        await SeedAsync("w", material: "wood");

        var page = await _persistence.QueryAsync(
            new WorkItemQuery(Materials: new[] { "plastic", "glass" }, Sort: "organisation"), ct);

        Assert.Equal(new[] { "g", "p" }, OrgOrder(page));
    }

    [Fact]
    public async Task QueryAsync_organisation_filter_matches_name_or_operator_org_id()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync("Acme Recycling", operatorOrganisationId: "ORG-111-001");
        await SeedAsync("Beta Metals", operatorOrganisationId: "ORG-222-002");

        var byName = await _persistence.QueryAsync(new WorkItemQuery(Organisation: "acme"), ct);
        Assert.Equal("Acme Recycling", Assert.Single(byName.Items).Payload["organisationName"].AsString);

        var byOrgId = await _persistence.QueryAsync(new WorkItemQuery(Organisation: "ORG-222"), ct);
        Assert.Equal("Beta Metals", Assert.Single(byOrgId.Items).Payload["organisationName"].AsString);
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

    // A C# null is rejected outright so it can't be written as an explicit
    // BSON null by accident; a deliberate null must be BsonNull.Value.
    [Fact]
    public async Task SetPayloadFieldAsync_rejects_a_null_value()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _persistence.SetPayloadFieldAsync(
                Guid.NewGuid(), "currentQuery", null!, TestContext.Current.CancellationToken));
    }
    // ------------------- RA-342 legacy snapshot tolerance -------------------

    /// <summary>
    /// RA-342: a work item whose frozen <c>templateSnapshot</c> was persisted
    /// under an older template carries a since-removed <c>requiredRoles</c>
    /// element on a transition (role tiering was dropped in RA-323). Before the
    /// fix, the default <c>BsonClassMapSerializer</c> rejected that unknown
    /// element with a <see cref="FormatException"/>, failing the whole worklist
    /// batch with a 500. Both the projected worklist query and the single-item
    /// fetch must now silently ignore the stray elements and return the item.
    ///
    /// The raw BSON is inserted directly (bypassing the typed serializer) so
    /// the stray elements land exactly as a legacy document holds them. Stray
    /// elements are seeded on every annotated snapshot type — the transition
    /// (the class in the reported stack trace), a state, a task and the
    /// snapshot root — so this single case exercises all four
    /// <c>[BsonIgnoreExtraElements]</c> annotations at once: if any were
    /// missing, deserialization would still throw.
    /// </summary>
    [Fact]
    public async Task QueryAsync_and_GetById_tolerate_a_legacy_snapshot_with_removed_fields()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = _time.GetUtcNow().UtcDateTime;
        var id = Guid.NewGuid();

        // Camel-cased element names to match the CamelCaseElementNameConvention
        // the production serializers register (MongoConversions).
        var legacyDoc = new BsonDocument
        {
            ["_id"] = id.ToString(),
            ["typeId"] = "re-accreditation",
            ["stateId"] = "submitted",
            ["submittedAt"] = new BsonDateTime(now),
            ["lastModifiedAt"] = new BsonDateTime(now),
            ["templateVersion"] = "1",
            ["version"] = 0,
            ["templateSnapshot"] = new BsonDocument
            {
                ["templateVersion"] = "1",
                // Snapshot-root stray element (WorkItemTemplateSnapshot).
                ["retiredSnapshotField"] = "legacy",
                ["states"] = new BsonArray
                {
                    // WorkItemState.Id maps to the "_id" element
                    // (default NamedIdMemberConvention), not "id".
                    new BsonDocument
                    {
                        ["_id"] = "submitted",
                        ["displayName"] = "Submitted",
                        ["isTerminal"] = false,
                        // State stray element (WorkItemState).
                        ["colour"] = "amber"
                    },
                    new BsonDocument
                    {
                        ["_id"] = "approved",
                        ["displayName"] = "Approved",
                        ["isTerminal"] = true
                    }
                },
                ["transitions"] = new BsonArray
                {
                    new BsonDocument
                    {
                        ["actionId"] = "approve",
                        ["displayName"] = "Approve",
                        ["fromStateId"] = "submitted",
                        ["toStateId"] = "approved",
                        ["requiresAllTasksComplete"] = true,
                        // The exact element from the reported stack trace: an
                        // array of role names on a transition (WorkItemTransition).
                        ["requiredRoles"] = new BsonArray { "regulator-admin", "regulator-approver" }
                    }
                },
                // RA-410: tasksByState itself is now a retired, whole-sale-ignored
                // element on the snapshot root — the task framework (and the type
                // that modelled a single task) is gone entirely, not just one of
                // its fields. A snapshot frozen before RA-410 still carries this
                // sub-document, and it must keep deserialising rather than
                // taking the whole worklist batch down with it.
                ["tasksByState"] = new BsonDocument
                {
                    ["submitted"] = new BsonArray
                    {
                        new BsonDocument
                        {
                            ["_id"] = "task-one",
                            ["displayName"] = "Task One",
                            ["mandatory"] = true
                        }
                    }
                }
            }
        };

        // Insert the raw document so the stray elements are stored verbatim,
        // exactly as a legacy work item holds them.
        await _clientFactory.GetCollection<BsonDocument>("workItems")
            .InsertOneAsync(legacyDoc, cancellationToken: ct);

        // Projected worklist path (WorkItemPersistence.QueryAsync) — the code
        // path in the reported 500. Must not throw and must return the item.
        var page = await _persistence.QueryAsync(new WorkItemQuery(), ct);
        var listed = Assert.Single(page.Items);
        Assert.Equal(id, listed.Id);
        Assert.NotNull(listed.TemplateSnapshot);
        // Everything else deserialized normally, the stray fields simply ignored.
        var transition = Assert.Single(listed.TemplateSnapshot!.Transitions);
        Assert.Equal("approve", transition.ActionId);
        Assert.Equal("submitted", transition.FromStateId);

        // Single-item fetch path (WorkItemPersistence.GetByIdAsync) must be
        // equally tolerant.
        var fetched = await _persistence.GetByIdAsync(id, ct);
        Assert.NotNull(fetched);
        Assert.Equal(id, fetched!.Id);
        Assert.NotNull(fetched.TemplateSnapshot);
        Assert.Equal("Submitted", Assert.Single(
            fetched.TemplateSnapshot!.States, s => s.Id == "submitted").DisplayName);
    }
}
