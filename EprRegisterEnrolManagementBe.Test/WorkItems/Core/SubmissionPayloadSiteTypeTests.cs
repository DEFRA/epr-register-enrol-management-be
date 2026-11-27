using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>RA-767: the type each site of a per-site submission is created as.</summary>
public sealed class SubmissionPayloadSiteTypeTests
{
    private static readonly TestWorkItemType s_add = Split("add", WorkItemCategories.InYearChange);
    private static readonly TestWorkItemType s_update = Split("update", WorkItemCategories.InYearChange);
    private static readonly TestWorkItemType s_otherCategory = Split("other", "other-category");
    private static readonly TestWorkItemType s_uncategorised = Split("uncategorised", category: null);
    private static readonly TestWorkItemType s_notSplit = new("whole", "Whole", category: WorkItemCategories.InYearChange);

    private static readonly IWorkItemRegistry s_registry = new WorkItemRegistry(
        [s_add, s_update, s_otherCategory, s_uncategorised, s_notSplit]
    );

    [Fact]
    public void A_site_takes_its_own_type_or_the_requests()
    {
        var payload = Payload(Site(), Site("update"), Site("add"));

        var types = SubmissionPayload.ResolveSiteTypes(payload, s_add, s_registry.Find, out var error);

        Assert.Null(error);
        Assert.Equal(["add", "update", "add"], types.Select(t => t.TypeId));
    }

    [Theory]
    [InlineData("no-such-type", SubmissionPayload.UnknownSiteTypeErrorCode)]
    [InlineData("whole", SubmissionPayload.UnknownSiteTypeErrorCode)]
    [InlineData(" ", SubmissionPayload.UnknownSiteTypeErrorCode)]
    [InlineData("other", SubmissionPayload.SiteTypeMismatchErrorCode)]
    [InlineData("uncategorised", SubmissionPayload.SiteTypeMismatchErrorCode)]
    public void A_site_type_that_cannot_be_used_is_refused(string typeId, string errorCode)
    {
        var payload = Payload(Site(), Site(typeId));

        var types = SubmissionPayload.ResolveSiteTypes(payload, s_add, s_registry.Find, out var error);

        Assert.Empty(types);
        Assert.Equal(errorCode, error!.ErrorCode);
        Assert.Contains("payload.sites[1].typeId", error.Message);
    }

    // A type with no category is only ever its own group.
    [Fact]
    public void An_uncategorised_request_type_accepts_only_itself()
    {
        var accepted = SubmissionPayload.ResolveSiteTypes(
            Payload(Site("uncategorised")),
            s_uncategorised,
            s_registry.Find,
            out var none
        );
        SubmissionPayload.ResolveSiteTypes(Payload(Site("add")), s_uncategorised, s_registry.Find, out var refused);

        Assert.Null(none);
        Assert.Equal(["uncategorised"], accepted.Select(t => t.TypeId));
        Assert.Equal(SubmissionPayload.SiteTypeMismatchErrorCode, refused!.ErrorCode);
    }

    [Fact]
    public void A_site_type_id_that_is_not_a_string_is_refused()
    {
        var site = Site();
        site[SubmissionPayload.SiteTypeIdField] = 5;

        SubmissionPayload.ResolveSiteTypes(Payload(site), s_add, s_registry.Find, out var error);

        Assert.Equal(SubmissionPayload.UnknownSiteTypeErrorCode, error!.ErrorCode);
    }

    private static TestWorkItemType Split(string typeId, string? category) =>
        new(typeId, typeId, category: category, splitsSubmissionPerSite: true);

    private static BsonDocument Site(string? typeId = null)
    {
        var site = new BsonDocument { ["siteId"] = Guid.NewGuid().ToString(), ["orsId"] = "ORS-1", ["siteName"] = "Site" };
        if (typeId is not null)
        {
            site[SubmissionPayload.SiteTypeIdField] = typeId;
        }
        return site;
    }

    private static BsonDocument Payload(params BsonDocument[] sites) =>
        new() { [SubmissionPayload.SitesField] = new BsonArray(sites) };
}
