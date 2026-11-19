using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EprRegisterEnrolManagementBe.Auth;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-739: <c>GET /work-items?category=</c> lists every work item whose type
/// is registered with that category, unioned with any <c>typeId</c> filter.
/// Driven through the real ASP.NET pipeline with the production modules
/// registered, against ephemeral MongoDB.
/// </summary>
public sealed class WorkItemCategoryFilterEndpointTests(MongoIntegrationFixture fixture)
{
    private const string ReAccreditation = "re-accreditation";
    private const string AddOverseasSite = "add-overseas-site";
    private const string FutureInYearChange = "future-in-year-change";

    private static readonly string[] s_addOverseasSiteOnly = [AddOverseasSite];
    private static readonly string[] s_addOverseasSiteAndReAccreditation =
    [
        AddOverseasSite,
        ReAccreditation,
    ];
    private static readonly string[] s_allInYearChangeTypes = [AddOverseasSite, FutureInYearChange];

    [Fact]
    public async Task The_in_year_change_category_lists_only_add_ORS_work_items()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new AppFactory(fixture);
        await factory.SeedAsync(cancellationToken, ReAccreditation, AddOverseasSite);

        var body = await factory.ListAsync("/work-items?category=in-year-change", cancellationToken);

        Assert.Equal(s_addOverseasSiteOnly, TypeIdsOf(body));
        Assert.Equal(1, body.TotalCount);
    }

    [Fact]
    public async Task The_category_is_unioned_with_a_typeId_filter()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new AppFactory(fixture);
        await factory.SeedAsync(cancellationToken, ReAccreditation, AddOverseasSite);

        var body = await factory.ListAsync(
            "/work-items?category=in-year-change&typeId=re-accreditation",
            cancellationToken
        );

        Assert.Equal(s_addOverseasSiteAndReAccreditation, TypeIdsOf(body));
        Assert.Equal(2, body.TotalCount);
    }

    [Fact]
    public async Task A_newly_registered_type_in_the_category_is_included_without_changing_the_filter()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new AppFactory(
            fixture,
            new TestWorkItemType(
                FutureInYearChange,
                "Future in-year change",
                category: WorkItemCategories.InYearChange
            )
        );
        await factory.SeedAsync(cancellationToken, ReAccreditation, AddOverseasSite, FutureInYearChange);

        var body = await factory.ListAsync("/work-items?category=in-year-change", cancellationToken);

        Assert.Equal(s_allInYearChangeTypes, TypeIdsOf(body));
    }

    [Fact]
    public async Task A_category_with_no_registered_types_lists_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new AppFactory(fixture);
        await factory.SeedAsync(cancellationToken, ReAccreditation, AddOverseasSite);

        var body = await factory.ListAsync("/work-items?category=no-such-category", cancellationToken);

        Assert.Empty(body.Items);
        Assert.Equal(0, body.TotalCount);
    }

    [Fact]
    public async Task The_category_combines_with_the_other_filters()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new AppFactory(fixture);
        await factory.SeedAsync(cancellationToken, ReAccreditation, AddOverseasSite);
        var dulyMade = await factory.SeedOneAsync(AddOverseasSite, "duly-made", cancellationToken);
        await factory.SeedOneAsync(ReAccreditation, "duly-made", cancellationToken);

        var body = await factory.ListAsync(
            "/work-items?category=in-year-change&stateId=duly-made",
            cancellationToken
        );

        var item = Assert.Single(body.Items);
        Assert.Equal(dulyMade, item.Id);
    }

    // Story 21: the Material, Organisation, Nation, Assignment and Sort
    // controls keep working with in-year change applications. Each add-ORS
    // item is posted through POST /work-items, so it carries the payload the
    // operator backend sends, and a re-accreditation item that would match the
    // same control proves the category still applies alongside it.

    [Fact]
    public async Task The_in_year_change_category_combines_with_a_lowercase_material_filter()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new AppFactory(fixture);
        // add-ORS sends the material enum name ("Plastic"); re-accreditation
        // and the Applications page send lowercase ("plastic").
        var plastic = await factory.SubmitAddOverseasSiteAsync(
            AddOverseasSiteSubmissions.Body("request-plastic", siteCount: 1, material: "Plastic"),
            cancellationToken
        );
        await factory.SubmitAddOverseasSiteAsync(
            AddOverseasSiteSubmissions.Body("request-glass", siteCount: 1, material: "Glass"),
            cancellationToken
        );
        await factory.SeedReAccreditationAsync(new BsonDocument("material", "plastic"), cancellationToken);

        var body = await factory.ListAsync(
            "/work-items?category=in-year-change&material=plastic",
            cancellationToken
        );

        var item = Assert.Single(body.Items);
        Assert.Equal(Assert.Single(plastic), item.Id);
    }

    [Fact]
    public async Task The_in_year_change_category_combines_with_the_organisation_filter()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new AppFactory(fixture);
        var acme = await factory.SubmitAddOverseasSiteAsync(
            AddOverseasSiteSubmissions.Body("request-acme", siteCount: 2, organisationName: "Acme Recycling Ltd"),
            cancellationToken
        );
        await factory.SubmitAddOverseasSiteAsync(
            AddOverseasSiteSubmissions.Body("request-zenith", siteCount: 1, organisationName: "Zenith Exports"),
            cancellationToken
        );
        await factory.SeedReAccreditationAsync(
            new BsonDocument("organisationName", "Acme Recycling Ltd"),
            cancellationToken
        );

        var body = await factory.ListAsync(
            "/work-items?category=in-year-change&organisation=acme",
            cancellationToken
        );

        Assert.Equal(acme.Order(), body.Items.Select(i => i.Id).Order());
    }

    [Fact]
    public async Task The_in_year_change_category_combines_with_the_orgName_search()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new AppFactory(fixture);
        var acme = await factory.SubmitAddOverseasSiteAsync(
            AddOverseasSiteSubmissions.Body("request-acme", siteCount: 1, organisationName: "Acme Recycling Ltd"),
            cancellationToken
        );
        await factory.SubmitAddOverseasSiteAsync(
            AddOverseasSiteSubmissions.Body("request-zenith", siteCount: 1, organisationName: "Zenith Exports"),
            cancellationToken
        );
        await factory.SeedReAccreditationAsync(
            new BsonDocument("organisationName", "Acme Recycling Ltd"),
            cancellationToken
        );

        var body = await factory.ListAsync(
            "/work-items?category=in-year-change&orgName=acme%20recycling",
            cancellationToken
        );

        var item = Assert.Single(body.Items);
        Assert.Equal(Assert.Single(acme), item.Id);
    }

    [Fact]
    public async Task The_in_year_change_category_combines_with_the_nation_filter()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new AppFactory(fixture);
        var wales = await factory.SubmitAddOverseasSiteAsync(
            AddOverseasSiteSubmissions.Body("request-wales", siteCount: 1, nation: "Wales"),
            cancellationToken
        );
        await factory.SubmitAddOverseasSiteAsync(
            AddOverseasSiteSubmissions.Body("request-england", siteCount: 1, nation: "England"),
            cancellationToken
        );
        await factory.SeedReAccreditationAsync(new BsonDocument("nation", "Wales"), cancellationToken);

        var body = await factory.ListAsync(
            "/work-items?category=in-year-change&nation=wales",
            cancellationToken
        );

        var item = Assert.Single(body.Items);
        Assert.Equal(Assert.Single(wales), item.Id);
    }

    [Fact]
    public async Task The_in_year_change_category_combines_with_the_assignment_filters()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new AppFactory(fixture);
        var ids = await factory.SubmitAddOverseasSiteAsync(
            AddOverseasSiteSubmissions.Body("request-assign", siteCount: 2),
            cancellationToken
        );
        var reAccreditation = await factory.SeedReAccreditationAsync(new BsonDocument(), cancellationToken);
        await factory.AssignToCallerAsync(ids[0], cancellationToken);
        await factory.AssignToCallerAsync(reAccreditation, cancellationToken);

        var mine = await factory.ListAsync(
            $"/work-items?category=in-year-change&assigneeId={AppFactory.CallerUserId}",
            cancellationToken
        );
        var unassigned = await factory.ListAsync(
            "/work-items?category=in-year-change&unassigned=true",
            cancellationToken
        );

        var assigned = Assert.Single(mine.Items);
        Assert.Equal(ids[0], assigned.Id);
        Assert.Equal(AppFactory.CallerUserId, assigned.AssignedToId);
        Assert.Equal(ids[1], Assert.Single(unassigned.Items).Id);
    }

    [Theory]
    [InlineData("", new[] { "Acme Recycling Ltd", "middleton metals", "Zenith Exports" })]
    [InlineData("&dir=desc", new[] { "Zenith Exports", "middleton metals", "Acme Recycling Ltd" })]
    public async Task The_in_year_change_category_combines_with_the_organisation_sort(
        string direction,
        string[] expectedOrder
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new AppFactory(fixture);
        // Lowercase "middleton" checks the sort is case-insensitive.
        string[] organisationNames = ["middleton metals", "Zenith Exports", "Acme Recycling Ltd"];
        foreach (var (organisationName, index) in organisationNames.Select((name, i) => (name, i)))
        {
            await factory.SubmitAddOverseasSiteAsync(
                AddOverseasSiteSubmissions.Body(
                    $"request-sort-{index}",
                    siteCount: 1,
                    organisationName: organisationName
                ),
                cancellationToken
            );
        }
        await factory.SeedReAccreditationAsync(
            new BsonDocument("organisationName", "Beta Plastics"),
            cancellationToken
        );

        var body = await factory.ListAsync(
            $"/work-items?category=in-year-change&sort=organisation{direction}",
            cancellationToken
        );

        Assert.Equal(
            expectedOrder,
            body.Items.Select(i => i.Payload.GetProperty("organisationName").GetString())
        );
    }

    private static string[] TypeIdsOf(WorkItemListResponse body) =>
        body.Items.Select(i => i.TypeId).Order(StringComparer.Ordinal).ToArray();

    private sealed class AppFactory(MongoIntegrationFixture fixture, params IWorkItemType[] extraTypes)
        : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = MongoIntegrationFixture.NewDatabaseName("category");

        public async Task SeedAsync(CancellationToken cancellationToken, params string[] typeIds)
        {
            foreach (var typeId in typeIds)
            {
                await SeedOneAsync(typeId, "submitted", cancellationToken);
            }
        }

        public async Task<Guid> SeedOneAsync(
            string typeId,
            string stateId,
            CancellationToken cancellationToken
        )
        {
            var item = new WorkItem
            {
                TypeId = typeId,
                StateId = stateId,
                SubmittedBy = "test-client",
            };
            await Services.GetRequiredService<IWorkItemPersistence>().CreateAsync(item, cancellationToken);
            return item.Id;
        }

        /// <summary>The caseworker every request is made as.</summary>
        public const string CallerUserId = "alice-1";

        /// <summary>Seed one re-accreditation work item carrying <paramref name="payload"/>.</summary>
        public async Task<Guid> SeedReAccreditationAsync(
            BsonDocument payload,
            CancellationToken cancellationToken
        )
        {
            var item = new WorkItem
            {
                TypeId = ReAccreditation,
                StateId = "submitted",
                SubmittedBy = "test-client",
                Payload = payload,
            };
            await Services.GetRequiredService<IWorkItemPersistence>().CreateAsync(item, cancellationToken);
            return item.Id;
        }

        /// <summary>Post an add-ORS submission and return its work item ids, in site order.</summary>
        public async Task<Guid[]> SubmitAddOverseasSiteAsync(
            JsonObject submission,
            CancellationToken cancellationToken
        )
        {
            using var client = CreateClient();
            var response = await client.PostAsJsonAsync("/work-items", submission, cancellationToken);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return body
                .GetProperty("workItems")
                .EnumerateArray()
                .Select(w => w.GetProperty("id").GetGuid())
                .ToArray();
        }

        /// <summary>Assign <paramref name="id"/> to <see cref="CallerUserId"/>.</summary>
        public async Task AssignToCallerAsync(Guid id, CancellationToken cancellationToken)
        {
            using var client = CreateClient();
            var response = await client.PostAsJsonAsync(
                $"/work-items/{id}/assign",
                new { assigneeId = CallerUserId, assigneeName = "Alice Example" },
                cancellationToken
            );
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        public async Task<WorkItemListResponse> ListAsync(string url, CancellationToken cancellationToken)
        {
            using var client = CreateClient();
            var response = await client.GetAsync(url, cancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<WorkItemListResponse>(cancellationToken);
            return body!;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("WorkItems:SeedOnStartup", "false");
            builder.ConfigureServices(services =>
            {
                services.UseEphemeralMongoPersistence(fixture, _databaseName);
                foreach (var type in extraTypes)
                {
                    services.AddSingleton(type);
                }
            });
        }

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, "test-client");
            // Submitting and assigning need a caseworker identity.
            client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultUserIdHeaderName, CallerUserId);
            client.DefaultRequestHeaders.Add("x-cdp-user-name", "Alice Example");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    Services
                        .GetRequiredService<IMongoDbClientFactory>()
                        .GetClient()
                        .DropDatabase(_databaseName);
                }
                catch
                {
                    // Best-effort; the ephemeral mongod dies with the fixture.
                }
            }
            base.Dispose(disposing);
        }
    }
}
