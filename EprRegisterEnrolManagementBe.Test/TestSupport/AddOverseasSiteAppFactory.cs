using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EprRegisterEnrolManagementBe.Auth;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Notifications;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.TestSupport;

/// <summary>
/// RA-495: the app with the production modules registered, against ephemeral
/// MongoDB, for the add-ORS lifecycle endpoint tests. Work items come from a
/// submission posted to <c>POST /work-items</c> as the operator backend posts
/// it. The operator-backend push adapter, the accreditation number adapter and
/// the Notify client are fakes, so a test can assert none of them is used.
/// </summary>
internal sealed class AddOverseasSiteAppFactory(MongoIntegrationFixture fixture) : WebApplicationFactory<Program>
{
    public const int SiteChargePence = 32800;

    private readonly string _databaseName = MongoIntegrationFixture.NewDatabaseName("addors");

    public IOperatorBackendPushAdapter PushAdapter { get; } = Substitute.For<IOperatorBackendPushAdapter>();

    public IAccreditationNumberAdapter NumberAdapter { get; } = Substitute.For<IAccreditationNumberAdapter>();

    public INotifyClient NotifyClient { get; } = Substitute.For<INotifyClient>();

    public IWorkItemPersistence Persistence => Services.GetRequiredService<IWorkItemPersistence>();

    /// <summary>Posts an add-ORS submission and returns its work item ids, in site order.</summary>
    public async Task<List<Guid>> SubmitAsync(
        HttpClient client,
        string operatorRequestId,
        int siteCount,
        CancellationToken ct
    )
    {
        var response = await client.PostAsJsonAsync(
            "/work-items",
            AddOverseasSiteSubmissions.Body(operatorRequestId, siteCount: siteCount, siteChargePence: SiteChargePence),
            ct
        );
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return body.GetProperty("workItems").EnumerateArray().Select(w => w.GetProperty("id").GetGuid()).ToList();
    }

    /// <summary>
    /// add-ORS has no operator-journey push target yet (RA-495 task 008). The
    /// status push runs in the background, so give it time to happen, then
    /// check nothing was pushed or recorded as pushed.
    /// </summary>
    public async Task AssertNothingPushedAsync(Guid workItemId, CancellationToken ct)
    {
        await Task.Delay(500, ct);
        Assert.Empty(PushAdapter.ReceivedCalls());
        var persisted = await Persistence.GetByIdAsync(workItemId, ct);
        Assert.DoesNotContain(persisted!.AuditLog, a => a.Action.StartsWith("status-push", StringComparison.Ordinal));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("WorkItems:SeedOnStartup", "false");
        // On, so a decision email would be sent if add-ORS registered a hook.
        builder.UseSetting("Notify:Enabled", "true");
        builder.ConfigureServices(services =>
        {
            services.UseEphemeralMongoPersistence(fixture, _databaseName);
            services.RemoveAll<IOperatorBackendPushAdapter>();
            services.AddSingleton(PushAdapter);
            services.RemoveAll<IAccreditationNumberAdapter>();
            services.AddSingleton(NumberAdapter);
            services.RemoveAll<INotifyClient>();
            services.AddSingleton(NotifyClient);
        });
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
                Services.GetRequiredService<IMongoDbClientFactory>().GetClient().DropDatabase(_databaseName);
            }
            catch
            {
                // Best-effort; the ephemeral mongod dies with the fixture.
            }
        }
        base.Dispose(disposing);
    }
}
