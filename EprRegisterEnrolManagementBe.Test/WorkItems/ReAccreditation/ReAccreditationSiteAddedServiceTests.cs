using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-294/RA-297: unit tests for <see cref="ReAccreditationSiteAddedService"/>.
/// Persistence and the audit appender are both substituted — this service is
/// deliberately thin, so the behaviour under test is entirely "find the work
/// item, append the audit entry with the right shape, never throw".
/// </summary>
public class ReAccreditationSiteAddedServiceTests
{
    private static SiteAddedRequest OrsRequest(string orsId = "001", bool isNewSite = true) =>
        new("ors", orsId, null, isNewSite);

    private static SiteAddedRequest InterimRequest(
        string orsId = "001",
        string siteNumber = "INT-1",
        bool isNewSite = true
    ) => new("interim", orsId, siteNumber, isNewSite);

    private static WorkItem BuildWorkItem(
        string typeId = ReAccreditationType.Id,
        Guid? id = null
    ) =>
        new()
        {
            Id = id ?? Guid.NewGuid(),
            TypeId = typeId,
            StateId = "assessment-in-progress",
            SubmittedBy = "test-client",
        };

    private sealed record Sut(
        ReAccreditationSiteAddedService Service,
        IWorkItemPersistence Persistence,
        IWorkItemAuditAppender AuditAppender
    );

    private static Sut Build(WorkItem? workItem = null, bool appendResult = true)
    {
        var persistence = Substitute.For<IWorkItemPersistence>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();

        persistence.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(workItem);

        auditAppender
            .AppendAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string?>>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(appendResult);

        var service = new ReAccreditationSiteAddedService(
            persistence,
            auditAppender,
            NullLogger<ReAccreditationSiteAddedService>.Instance
        );

        return new Sut(service, persistence, auditAppender);
    }

    private static ClaimsPrincipal SystemUser() =>
        new(new ClaimsIdentity([new Claim("client_id", "operator-backend")], "test"));

    // ------------------------------ not found ------------------------------

    [Fact]
    public async Task RecordSiteAddedAsync_fails_with_work_item_not_found_when_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build(workItem: null);

        var result = await sut.Service.RecordSiteAddedAsync(
            Guid.NewGuid(),
            OrsRequest(),
            SystemUser(),
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
        await sut
            .AuditAppender.DidNotReceiveWithAnyArgs()
            .AppendAsync(default, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task RecordSiteAddedAsync_fails_when_work_item_is_wrong_type()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem(typeId: "some-other-type");
        var sut = Build(workItem);

        var result = await sut.Service.RecordSiteAddedAsync(
            workItem.Id,
            OrsRequest(),
            SystemUser(),
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.UnknownAction, result.FailureCode);
        await sut
            .AuditAppender.DidNotReceiveWithAnyArgs()
            .AppendAsync(default, default!, default!, default!, default!, default);
    }

    // -------------------------------- success -------------------------------

    [Fact]
    public async Task RecordSiteAddedAsync_appends_a_site_added_audit_entry_for_an_ors_site()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var sut = Build(workItem);
        var user = SystemUser();

        var result = await sut.Service.RecordSiteAddedAsync(
            workItem.Id,
            OrsRequest(orsId: "001", isNewSite: true),
            user,
            ct
        );

        Assert.True(result.IsSuccess);
        await sut
            .AuditAppender.Received(1)
            .AppendAsync(
                workItem.Id,
                ReAccreditationSiteAddedService.AuditAction,
                ReAccreditationSiteAddedService.AuditActionDisplayName,
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["siteType"] == "ors"
                    && d["orsId"] == "001"
                    && d["siteNumber"] == null
                    && d["isNewSite"] == "True"
                ),
                user,
                ct
            );
    }

    [Fact]
    public async Task RecordSiteAddedAsync_appends_a_site_added_audit_entry_for_an_interim_site()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var sut = Build(workItem);
        var user = SystemUser();

        var result = await sut.Service.RecordSiteAddedAsync(
            workItem.Id,
            InterimRequest(orsId: "001", siteNumber: "INT-1", isNewSite: false),
            user,
            ct
        );

        Assert.True(result.IsSuccess);
        await sut
            .AuditAppender.Received(1)
            .AppendAsync(
                workItem.Id,
                "site-added",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["siteType"] == "interim"
                    && d["orsId"] == "001"
                    && d["siteNumber"] == "INT-1"
                    && d["isNewSite"] == "False"
                ),
                user,
                ct
            );
    }

    [Fact]
    public async Task RecordSiteAddedAsync_returns_the_refreshed_work_item_on_success()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var refreshed = BuildWorkItem(id: workItem.Id);

        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence
            .GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem, refreshed);
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        auditAppender
            .AppendAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string?>>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(true);
        var service = new ReAccreditationSiteAddedService(
            persistence,
            auditAppender,
            NullLogger<ReAccreditationSiteAddedService>.Instance
        );

        var result = await service.RecordSiteAddedAsync(
            workItem.Id,
            OrsRequest(),
            SystemUser(),
            ct
        );

        Assert.True(result.IsSuccess);
        Assert.Same(refreshed, result.WorkItem);
    }

    // --------------------------- append failure paths ------------------------

    [Fact]
    public async Task RecordSiteAddedAsync_fails_with_concurrency_conflict_when_append_returns_false()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var sut = Build(workItem, appendResult: false);

        var result = await sut.Service.RecordSiteAddedAsync(
            workItem.Id,
            OrsRequest(),
            SystemUser(),
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.ConcurrencyConflict, result.FailureCode);
    }

    [Fact]
    public async Task RecordSiteAddedAsync_never_throws_when_the_audit_appender_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        auditAppender
            .AppendAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string?>>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .ThrowsAsync(new InvalidOperationException("boom"));
        var service = new ReAccreditationSiteAddedService(
            persistence,
            auditAppender,
            NullLogger<ReAccreditationSiteAddedService>.Instance
        );

        // Mirrors ReAccreditationQueryPushHook's own contract: an unexpected
        // failure while appending the audit entry must surface as a
        // controlled failure result, not an unhandled exception.
        var result = await service.RecordSiteAddedAsync(
            workItem.Id,
            OrsRequest(),
            SystemUser(),
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.ConcurrencyConflict, result.FailureCode);
    }
}
