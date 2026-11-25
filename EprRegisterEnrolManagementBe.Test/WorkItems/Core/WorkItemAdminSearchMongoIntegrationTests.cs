using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-751: the Admin UI's work item search filters of <c>GET /work-items</c>
/// (<c>q</c>, <c>assigned</c>, <c>archived</c>) against real Mongo.
/// RA-751 AC4: the search box matches a work item ID, an accreditation ID or
/// RA- reference, an organisation ID or part of an organisation name.
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
        Guid? id = null
    )
    {
        var item = new WorkItem
        {
            Id = id ?? Guid.NewGuid(),
            TypeId = "re-accreditation",
            StateId = "submitted",
            SubmittedAt = s_now,
            LastModifiedAt = s_now,
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
    public async Task Q_matches_the_ra_reference_case_insensitively()
    {
        await SeedAsync("Target", new BsonDocument { ["applicationReference"] = "RA-900000751" });
        await SeedAsync("Other", new BsonDocument { ["applicationReference"] = "RA-100000001" });

        Assert.Equal(["Target"], await NamesMatchingAsync(new WorkItemQuery(Q: "ra-900000751")));
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
}
