using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-751: the Admin UI's work item search filters of <c>GET /work-items</c>
/// (<c>q</c>, <c>assigned</c>, <c>archived</c>) against real Mongo.
/// RA-751 AC4: the search box matches a work item ID, an accreditation ID or
/// RA- reference, an organisation ID or part of an organisation name. The
/// reference a work item carries is <c>payload.applicationReference</c>
/// (<c>AP…</c> from ApplicationReferenceGenerator).
/// </summary>
public sealed class WorkItemAdminSearchMongoIntegrationTests : IAsyncDisposable
{
    private static readonly DateTime s_now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _databaseName;
    private readonly TestMongoDbClientFactory _clientFactory;
    private readonly WorkItemPersistence _persistence;

    public WorkItemAdminSearchMongoIntegrationTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("work_items_admin_search");
        _clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _persistence = new WorkItemPersistence(_clientFactory, NullLoggerFactory.Instance);
    }

    public async ValueTask DisposeAsync() =>
        await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);

    private async Task<WorkItem> SeedAsync(
        string name,
        BsonDocument? payload = null,
        string? assignedToId = null,
        Guid? id = null,
        DateTime? lastModifiedAt = null,
        DateTime? submittedAt = null
    )
    {
        var item = new WorkItem
        {
            Id = id ?? Guid.NewGuid(),
            TypeId = "re-accreditation",
            StateId = "submitted",
            SubmittedAt = submittedAt ?? s_now,
            LastModifiedAt = lastModifiedAt ?? s_now,
            SubmittedBy = "client-1",
            AssignedToId = assignedToId,
            Payload = payload ?? new BsonDocument(),
        };
        item.Payload["organisationName"] = item.Payload.GetValue("organisationName", name);
        await _persistence.CreateAsync(item, TestContext.Current.CancellationToken);
        return item;
    }

    private async Task<string[]> NamesMatchingAsync(WorkItemQuery query)
    {
        var page = await _persistence.QueryAsync(query, TestContext.Current.CancellationToken);
        return page.Items.Select(i => i.Payload["organisationName"].AsString).Order().ToArray();
    }

    [Fact]
    public async Task Q_matches_part_of_the_work_item_id()
    {
        await SeedAsync("Target", id: Guid.Parse("7a51c0de-0000-4000-8000-000000000751"));
        await SeedAsync("Other", id: Guid.Parse("11111111-0000-4000-8000-000000000001"));

        Assert.Equal(["Target"], await NamesMatchingAsync(new WorkItemQuery(Q: "7A51C0DE")));
    }

    [Fact]
    public async Task Q_matches_part_of_the_accreditation_id()
    {
        await SeedAsync("Target", new BsonDocument { ["accreditationId"] = "A27ER9006910246PL" });
        await SeedAsync("Other", new BsonDocument { ["accreditationId"] = "A27ER1234560001GL" });

        Assert.Equal(["Target"], await NamesMatchingAsync(new WorkItemQuery(Q: "9006910246")));
    }

    [Fact]
    public async Task Q_matches_the_application_reference_case_insensitively()
    {
        await SeedAsync("Target", new BsonDocument { ["applicationReference"] = "APEPL5005001" });
        await SeedAsync("Other", new BsonDocument { ["applicationReference"] = "APEGL5009002" });

        Assert.Equal(["Target"], await NamesMatchingAsync(new WorkItemQuery(Q: "apepl5005")));
    }

    [Fact]
    public async Task Q_matches_the_organisation_id()
    {
        await SeedAsync("Target", new BsonDocument { ["operatorOrganisationId"] = "e2e-ra751-archived" });
        await SeedAsync("Other", new BsonDocument { ["operatorOrganisationId"] = "e2e-ra691-accreditation-corrected" });

        Assert.Equal(["Target"], await NamesMatchingAsync(new WorkItemQuery(Q: "ra751")));
    }

    [Fact]
    public async Task Q_matches_part_of_the_organisation_name()
    {
        await SeedAsync("Acme Plastics Ltd");
        await SeedAsync("Glass Works");

        Assert.Equal(["Acme Plastics Ltd"], await NamesMatchingAsync(new WorkItemQuery(Q: "plastics")));
    }

    [Fact]
    public async Task Q_treats_regex_characters_literally()
    {
        await SeedAsync("A.B Ltd");
        await SeedAsync("AxB Ltd");

        Assert.Equal(["A.B Ltd"], await NamesMatchingAsync(new WorkItemQuery(Q: "A.B")));
    }

    [Fact]
    public async Task Q_does_not_match_the_submitter()
    {
        // submittedBy is matched by 'search' (management-fe), not by 'q'.
        await SeedAsync("Target");

        Assert.Empty(await NamesMatchingAsync(new WorkItemQuery(Q: "client-1")));
    }

    [Fact]
    public async Task Assigned_true_returns_items_with_any_assignee_and_false_returns_unassigned_items()
    {
        await SeedAsync("Alice's", assignedToId: "alice");
        await SeedAsync("Bob's", assignedToId: "bob");
        await SeedAsync("Nobody's");

        Assert.Equal(["Alice's", "Bob's"], await NamesMatchingAsync(new WorkItemQuery(Assigned: true)));
        Assert.Equal(["Nobody's"], await NamesMatchingAsync(new WorkItemQuery(Assigned: false)));
    }

    [Fact]
    public async Task Archived_true_returns_items_with_archived_at_and_false_returns_the_rest()
    {
        await SeedAsync("Archived", new BsonDocument { ["archivedAt"] = new BsonDateTime(s_now) });
        await SeedAsync("Live");

        Assert.Equal(["Archived"], await NamesMatchingAsync(new WorkItemQuery(Archived: true)));
        Assert.Equal(["Live"], await NamesMatchingAsync(new WorkItemQuery(Archived: false)));
    }

    [Fact]
    public async Task Absent_assigned_and_archived_do_not_filter()
    {
        await SeedAsync("Archived and assigned", new BsonDocument { ["archivedAt"] = new BsonDateTime(s_now) }, "alice");
        await SeedAsync("Live and unassigned");

        Assert.Equal(
            ["Archived and assigned", "Live and unassigned"],
            await NamesMatchingAsync(new WorkItemQuery())
        );
    }

    // RA-751 AC8 ("as in RA-740"): most recently updated first.
    private async Task SeedLastModifiedAsync()
    {
        // Submitted in the opposite order to last-modified, so the default
        // (newest submitted) and the last-modified sort disagree.
        await SeedAsync("Changed 1h ago", lastModifiedAt: s_now.AddHours(-1), submittedAt: s_now.AddDays(-3));
        await SeedAsync("Changed 3h ago", lastModifiedAt: s_now.AddHours(-3), submittedAt: s_now.AddDays(-1));
        await SeedAsync("Changed 2h ago", lastModifiedAt: s_now.AddHours(-2), submittedAt: s_now.AddDays(-2));
    }

    private async Task<string[]> OrderedNamesAsync(WorkItemQuery query)
    {
        var page = await _persistence.QueryAsync(query, TestContext.Current.CancellationToken);
        return page.Items.Select(i => i.Payload["organisationName"].AsString).ToArray();
    }

    [Fact]
    public async Task Last_modified_sort_is_newest_first_by_default_and_with_dir_desc()
    {
        await SeedLastModifiedAsync();
        string[] newestFirst = ["Changed 1h ago", "Changed 2h ago", "Changed 3h ago"];

        Assert.Equal(newestFirst, await OrderedNamesAsync(new WorkItemQuery(Sort: "last-modified")));
        Assert.Equal(
            newestFirst,
            await OrderedNamesAsync(new WorkItemQuery(Sort: "last-modified", SortDescending: true))
        );
    }

    [Fact]
    public async Task Last_modified_sort_with_dir_asc_is_oldest_first()
    {
        await SeedLastModifiedAsync();

        Assert.Equal(
            ["Changed 3h ago", "Changed 2h ago", "Changed 1h ago"],
            await OrderedNamesAsync(new WorkItemQuery(Sort: "last-modified", SortDescending: false))
        );
    }

    [Fact]
    public async Task Last_modified_sort_breaks_ties_on_id_in_the_same_direction_and_pages_stably()
    {
        var low = Guid.Parse("00000000-0000-4000-8000-000000000001");
        var mid = Guid.Parse("00000000-0000-4000-8000-000000000002");
        var high = Guid.Parse("00000000-0000-4000-8000-000000000003");
        await SeedAsync("Mid", id: mid);
        await SeedAsync("High", id: high);
        await SeedAsync("Low", id: low);

        Assert.Equal(["High", "Mid", "Low"], await OrderedNamesAsync(new WorkItemQuery(Sort: "last-modified")));
        Assert.Equal(
            ["Low", "Mid", "High"],
            await OrderedNamesAsync(new WorkItemQuery(Sort: "last-modified", SortDescending: false))
        );
        Assert.Equal(
            ["Mid"],
            await OrderedNamesAsync(new WorkItemQuery(Sort: "last-modified", Page: 2, PageSize: 1))
        );
    }

    [Fact]
    public async Task Last_modified_sort_leaves_notes_and_audit_log_out_of_the_page()
    {
        var item = new WorkItem
        {
            TypeId = "re-accreditation",
            StateId = "submitted",
            SubmittedAt = s_now,
            LastModifiedAt = s_now,
            Payload = new BsonDocument { ["organisationName"] = "With history" },
            Notes = [new WorkItemNote { Text = "note", CreatedAt = s_now, CreatedBy = "u" }],
        };
        await _persistence.CreateAsync(item, TestContext.Current.CancellationToken);
        Assert.Single((await _persistence.GetByIdAsync(item.Id, TestContext.Current.CancellationToken))!.Notes);

        var page = await _persistence.QueryAsync(
            new WorkItemQuery(Sort: "last-modified"),
            TestContext.Current.CancellationToken
        );

        Assert.Empty(Assert.Single(page.Items).Notes);
    }

    [Fact]
    public async Task Default_order_is_still_newest_submitted_first()
    {
        await SeedLastModifiedAsync();

        Assert.Equal(
            ["Changed 3h ago", "Changed 2h ago", "Changed 1h ago"],
            await OrderedNamesAsync(new WorkItemQuery())
        );
    }
}
