using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EprRegisterEnrolManagementBe.Auth;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.AddOverseasSite;

/// <summary>
/// RA-739: an add-ORS submission posted to <c>POST /work-items</c> becomes one
/// work item per site. Driven through the real ASP.NET pipeline with the
/// production modules registered, against ephemeral MongoDB.
/// </summary>
public sealed class AddOverseasSiteSubmissionEndpointTests(MongoIntegrationFixture fixture)
{
    private const string TypeId = "add-overseas-site";

    private static object Submission(
        string operatorRequestId,
        int accreditationYear = 2026,
        int siteCount = 3
    ) =>
        new
        {
            typeId = TypeId,
            source = "operator-fe",
            payload = new
            {
                operatorRequestId,
                changeType = "add-overseas-site",
                operatorOrganisationId = "org-1",
                organisationName = "Acme Recycling Ltd",
                operatorOrgNumber = 500500,
                material = "Plastic",
                accreditationYear,
                chargePence = 300000,
                paymentReference = "PR/PK/REP/500500",
                sites = Enumerable
                    .Range(1, siteCount)
                    .Select(n => new
                    {
                        siteId = $"site-{n}",
                        orsId = $"ORS-{n:D3}",
                        siteName = $"Overseas site {n}",
                        country = "France",
                    })
                    .ToArray(),
            },
        };

    [Fact]
    public async Task Posting_three_sites_creates_three_submitted_work_items_sharing_one_submission_reference()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new AppFactory(fixture);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/work-items",
            Submission("request-1"),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal("SUB-2026-0001", body.GetProperty("submissionReference").GetString());
        var workItems = body.GetProperty("workItems").EnumerateArray().ToList();
        Assert.Equal(
            ["site-1", "site-2", "site-3"],
            workItems.Select(w => w.GetProperty("siteId").GetString())
        );
        Assert.Equal(
            ["IYC-ORS-2026-00001", "IYC-ORS-2026-00002", "IYC-ORS-2026-00003"],
            workItems.Select(w => w.GetProperty("applicationReference").GetString())
        );

        foreach (var (entry, index) in workItems.Select((w, i) => (w, i)))
        {
            var stored = await factory.Persistence.GetByIdAsync(
                entry.GetProperty("id").GetGuid(),
                cancellationToken
            );
            Assert.NotNull(stored);
            Assert.Equal(TypeId, stored!.TypeId);
            Assert.Equal("submitted", stored.StateId);
            var payload = stored.Payload;
            Assert.Equal(
                entry.GetProperty("applicationReference").GetString(),
                payload["applicationReference"].AsString
            );
            Assert.Equal("SUB-2026-0001", payload["submissionReference"].AsString);
            Assert.Equal(3, payload["submissionSize"].ToInt32());
            Assert.Equal("PR/PK/REP/500500", payload["paymentReference"].AsString);
            Assert.Equal("request-1", payload["operatorRequestId"].AsString);
            Assert.Equal("Acme Recycling Ltd", payload["organisationName"].AsString);
            Assert.Equal("Plastic", payload["material"].AsString);
            Assert.False(payload.Contains("sites"));
            var site = payload["site"].AsBsonDocument;
            Assert.Equal($"site-{index + 1}", site["siteId"].AsString);
            Assert.Equal($"ORS-{index + 1:D3}", site["orsId"].AsString);
            Assert.Equal($"Overseas site {index + 1}", site["siteName"].AsString);
            Assert.Equal("France", site["country"].AsString);
        }
    }

    // RA-739: the operator backend sends each site in re-accreditation's site
    // shape plus its own charge. The Case Management service checks only orsId
    // and siteName, so the rest of the site, interim sites and BES files
    // included, is stored as sent.
    [Fact]
    public async Task Each_work_item_keeps_its_sites_charge_interim_sites_and_bes_files_as_sent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new AppFactory(fixture);
        using var client = factory.CreateClient();
        var submission = new
        {
            typeId = TypeId,
            source = "operator-fe",
            payload = new
            {
                operatorRequestId = "request-full-site",
                changeType = "add-overseas-site",
                operatorOrganisationId = "org-1",
                organisationName = "Acme Recycling Ltd",
                operatorOrgNumber = 500500,
                material = "Plastic",
                accreditationYear = 2026,
                chargePence = 65600,
                paymentReference = "PR/PK/REP/500500",
                sites = new[] { 901910, 901911 }
                    .Select(siteId => new
                    {
                        siteId,
                        orsId = $"ORS-{siteId}",
                        siteName = $"Overseas site {siteId}",
                        addressLine1 = "1 Rue Example",
                        townOrCity = "Lyon",
                        country = "France",
                        coordinates = "45.7640, 4.8357",
                        contactName = "Site Manager",
                        operationCodes = new[] { "R3" },
                        code1 = "B3011",
                        conditionsOfExport = false,
                        isEu = true,
                        isOecd = true,
                        interimSites = new object[]
                        {
                            new { siteId = 1, siteNumber = "001", siteName = "Marseille Storage" },
                            new
                            {
                                siteId = 2,
                                siteNumber = "002",
                                siteName = "Old Depot",
                                removedAt = "2026-02-01T09:00:00Z",
                            },
                        },
                        besEvidence = new
                        {
                            files = new[]
                            {
                                new
                                {
                                    fileId = $"file-{siteId}",
                                    filename = "bes.pdf",
                                    contentType = "application/pdf",
                                    scanStatus = "Clean",
                                    besEvidenceValidFromDate = "2026-01-01",
                                    besEvidenceExpiryDate = "2027-12-31",
                                    s3Key = $"bes-evidence/file-{siteId}",
                                    s3Bucket = "bes-bucket",
                                },
                            },
                        },
                        suppliedFields = new[] { "conditionsOfExport" },
                        chargePence = 32800,
                    })
                    .ToArray(),
            },
        };

        var response = await client.PostAsJsonAsync("/work-items", submission, cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var ids = body.GetProperty("workItems")
            .EnumerateArray()
            .Select(w => w.GetProperty("id").GetGuid())
            .ToList();
        Assert.Equal(2, ids.Count);
        foreach (var id in ids)
        {
            var stored = await factory.Persistence.GetByIdAsync(id, cancellationToken);
            Assert.NotNull(stored);
            Assert.Equal(65600, stored!.Payload["chargePence"].ToInt32());
            var site = stored.Payload["site"].AsBsonDocument;
            var siteId = site["siteId"].ToInt32();
            Assert.Equal(32800, site["chargePence"].ToInt32());
            Assert.Equal("Site Manager", site["contactName"].AsString);
            Assert.Equal("B3011", site["code1"].AsString);
            Assert.Equal(["conditionsOfExport"], site["suppliedFields"].AsBsonArray.Select(f => f.AsString));

            var interimSites = site["interimSites"].AsBsonArray.Select(i => i.AsBsonDocument).ToList();
            Assert.Equal(["Marseille Storage", "Old Depot"], interimSites.Select(i => i["siteName"].AsString));
            Assert.False(interimSites[0].Contains("removedAt"));
            Assert.True(interimSites[1].Contains("removedAt"));

            var file = Assert.Single(site["besEvidence"]["files"].AsBsonArray).AsBsonDocument;
            Assert.Equal($"file-{siteId}", file["fileId"].AsString);
            Assert.Equal("bes.pdf", file["filename"].AsString);
            Assert.Equal("Clean", file["scanStatus"].AsString);
            Assert.Equal("2026-01-01", file["besEvidenceValidFromDate"].AsString);
            Assert.Equal("2027-12-31", file["besEvidenceExpiryDate"].AsString);
            Assert.Equal($"bes-evidence/file-{siteId}", file["s3Key"].AsString);
            Assert.Equal("bes-bucket", file["s3Bucket"].AsString);
        }
    }

    [Fact]
    public async Task The_submission_reference_is_sequential_per_accreditation_year()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new AppFactory(fixture);
        using var client = factory.CreateClient();

        var first = await PostAsync(client, Submission("request-a", siteCount: 1));
        var second = await PostAsync(client, Submission("request-b", siteCount: 2));
        var nextYear = await PostAsync(client, Submission("request-c", accreditationYear: 2027));

        Assert.Equal("SUB-2026-0001", first.GetProperty("submissionReference").GetString());
        Assert.Equal("SUB-2026-0002", second.GetProperty("submissionReference").GetString());
        Assert.Equal("SUB-2027-0001", nextYear.GetProperty("submissionReference").GetString());
        Assert.Equal(
            ["IYC-ORS-2026-00002", "IYC-ORS-2026-00003"],
            second
                .GetProperty("workItems")
                .EnumerateArray()
                .Select(w => w.GetProperty("applicationReference").GetString())
        );

        async Task<JsonElement> PostAsync(HttpClient http, object submission)
        {
            var response = await http.PostAsJsonAsync("/work-items", submission, cancellationToken);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        }
    }

    [Fact]
    public async Task A_single_site_submission_has_a_submission_size_of_one()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new AppFactory(fixture);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/work-items",
            Submission("request-single", siteCount: 1),
            cancellationToken
        );

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var id = Assert.Single(body.GetProperty("workItems").EnumerateArray()).GetProperty("id").GetGuid();
        var stored = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal(1, stored!.Payload["submissionSize"].ToInt32());
    }

    [Fact]
    public async Task Each_work_item_is_listed_with_its_payload_and_the_new_ORS_display_name()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new AppFactory(fixture);
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/work-items", Submission("request-list"), cancellationToken);

        var list = await client.GetFromJsonAsync<JsonElement>(
            $"/work-items?typeId={TypeId}",
            cancellationToken
        );

        var items = list.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);
        Assert.All(
            items,
            item =>
            {
                Assert.Equal(TypeId, item.GetProperty("typeId").GetString());
                Assert.Equal("submitted", item.GetProperty("stateId").GetString());
                var payload = item.GetProperty("payload");
                Assert.Equal("SUB-2026-0001", payload.GetProperty("submissionReference").GetString());
                Assert.Equal(3, payload.GetProperty("submissionSize").GetInt32());
            }
        );
    }

    [Fact]
    public async Task Re_accreditation_submissions_get_no_submission_reference_and_keep_their_response()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new AppFactory(fixture);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/work-items",
            new
            {
                typeId = "re-accreditation",
                payload = new
                {
                    accreditationYear = 2026,
                    nation = "England",
                    operatorOrgNumber = 500500,
                    siteAddressPostcode = "SW1A 1AB",
                    material = "Plastic",
                },
            },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.False(body.TryGetProperty("submissionReference", out _));
        var stored = await factory.Persistence.GetByIdAsync(
            body.GetProperty("id").GetGuid(),
            cancellationToken
        );
        Assert.False(stored!.Payload.Contains("submissionReference"));
        Assert.Equal(0, await factory.CountSequencesAsync(cancellationToken));
    }

    [Fact]
    public async Task Startup_replaces_the_single_field_operatorRequestId_index_with_one_per_site()
    {
        // RA-739: the RA-492 index allowed one work item per operatorRequestId.
        // A deployed database still carries it, and it must go, or every
        // site after the first in a submission collides.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new AppFactory(fixture);
        var workItems = new MongoClient(fixture.ConnectionString)
            .GetDatabase(factory.DatabaseName)
            .GetCollection<BsonDocument>("workItems");
        await workItems.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                new BsonDocument("payload.operatorRequestId", 1),
                new CreateIndexOptions<BsonDocument>
                {
                    Unique = true,
                    PartialFilterExpression = new BsonDocument(
                        "payload.operatorRequestId",
                        new BsonDocument("$type", "string")
                    ),
                }
            ),
            cancellationToken: cancellationToken
        );

        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/work-items",
            Submission("request-index"),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var indexes = await (await workItems.Indexes.ListAsync(cancellationToken)).ToListAsync(
            cancellationToken
        );
        Assert.DoesNotContain(indexes, i => i["name"].AsString == "payload.operatorRequestId_1");
        var perSite = Assert.Single(
            indexes,
            i =>
                i["key"].AsBsonDocument
                == new BsonDocument { ["payload.operatorRequestId"] = 1, ["payload.site.siteId"] = 1 }
        );
        Assert.True(perSite["unique"].AsBoolean);
    }

    private sealed class AppFactory(MongoIntegrationFixture fixture) : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = MongoIntegrationFixture.NewDatabaseName("addors");

        public string DatabaseName => _databaseName;

        public IWorkItemPersistence Persistence => Services.GetRequiredService<IWorkItemPersistence>();

        public Task<long> CountSequencesAsync(CancellationToken cancellationToken) =>
            Services
                .GetRequiredService<IMongoDbClientFactory>()
                .GetCollection<BsonDocument>(MongoReferenceSequenceStore.CollectionName)
                .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: cancellationToken);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("WorkItems:SeedOnStartup", "false");
            builder.ConfigureServices(services =>
                services.UseEphemeralMongoPersistence(fixture, _databaseName)
            );
        }

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, "test-client");
            client.DefaultRequestHeaders.Add("x-cdp-user-id", "alice-1");
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
