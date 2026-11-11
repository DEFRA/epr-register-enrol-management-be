using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolManagementBe.Auth;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

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
