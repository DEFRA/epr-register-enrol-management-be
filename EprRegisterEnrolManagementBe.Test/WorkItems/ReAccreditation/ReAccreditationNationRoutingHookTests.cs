using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

public class ReAccreditationNationRoutingHookTests
{
    private static readonly ClaimsPrincipal s_user = new(new ClaimsIdentity(
    [
        new Claim("user:id", "user-1"),
        new Claim("user:name", "Alice")
    ], "test"));

    private static readonly DateTime s_now = new(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static WorkItem BuildWorkItem(string? postcode = "EH1 1AA")
    {
        var payload = new BsonDocument
        {
            ["organisationName"] = "Acme Ltd"
        };
        if (postcode is not null)
        {
            payload["siteAddressPostcode"] = postcode;
        }

        return new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            Payload = payload,
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType()),
            TemplateVersion = "v3"
        };
    }

    private static ReAccreditationNationRoutingHook BuildSut(
        IWorkItemPersistence persistence,
        INationResolver? resolver = null,
        FakeTimeProvider? clock = null,
        TimeSpan? retryDelay = null)
    {
        resolver ??= new NationResolver();
        clock ??= new FakeTimeProvider(s_now);
        // TimeSpan.Zero by default to keep retry tests fast and deterministic.
        retryDelay ??= TimeSpan.Zero;
        return new ReAccreditationNationRoutingHook(
            resolver,
            persistence,
            NullLogger<ReAccreditationNationRoutingHook>.Instance,
            clock,
            retryDelay);
    }

    // ─────────────────────────── OnSubmittedAsync ───────────────────────────

    [Theory]
    [InlineData("EH1 1AA", "Scotland")]
    [InlineData("CF10 1AA", "Wales")]
    [InlineData("BT1 1AA", "NorthernIreland")]
    [InlineData("SW1A 1AA", "England")]
    public async Task OnSubmittedAsync_sets_Nation_in_payload_and_appends_audit_entry(
        string postcode, string expectedNation)
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem(postcode);
        var freshCopy = BuildWorkItem(postcode);
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.GetByIdAsync(workItem.Id, ct).Returns(freshCopy);

        var sut = BuildSut(persistence);
        await sut.OnSubmittedAsync(workItem, s_user, ct);

        await persistence.Received(1).ReplaceAsync(
            Arg.Is<WorkItem>(w =>
                w.Payload["nation"].AsString == expectedNation),
            ct);

        var entry = freshCopy.AuditLog.Single();
        Assert.Equal("routed-to-nation", entry.Action);
        Assert.Equal(expectedNation, entry.Details["nation"]);
        Assert.Equal("site-address", entry.Details["derivedFrom"]);
        Assert.Equal("user-1", entry.CreatedBy);
        Assert.Equal("Alice", entry.CreatedByName);
        Assert.Equal(s_now, entry.CreatedAt);
    }

    [Fact]
    public async Task OnSubmittedAsync_defaults_to_England_when_postcode_absent()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem(postcode: null);
        var freshCopy = BuildWorkItem(postcode: null);
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.GetByIdAsync(workItem.Id, ct).Returns(freshCopy);

        var sut = BuildSut(persistence);
        await sut.OnSubmittedAsync(workItem, s_user, ct);

        await persistence.Received(1).ReplaceAsync(
            Arg.Is<WorkItem>(w => w.Payload["nation"].AsString == "England"), ct);
    }

    [Fact]
    public async Task OnSubmittedAsync_skips_non_re_accreditation_work_items()
    {
        var ct = TestContext.Current.CancellationToken;
        var persistence = Substitute.For<IWorkItemPersistence>();
        var workItem = new WorkItem
        {
            TypeId = "other-type",
            StateId = "submitted",
            Payload = new BsonDocument(),
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType()),
            TemplateVersion = "v3"
        };

        var sut = BuildSut(persistence);
        await sut.OnSubmittedAsync(workItem, s_user, ct);

        await persistence.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnSubmittedAsync_silently_aborts_when_work_item_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.GetByIdAsync(workItem.Id, ct).Returns((WorkItem?)null);

        var sut = BuildSut(persistence);
        // Must not throw.
        await sut.OnSubmittedAsync(workItem, s_user, ct);

        await persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnSubmittedAsync_retries_on_concurrency_exception_and_succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem("G1 1AA");
        var persistence = Substitute.For<IWorkItemPersistence>();

        var callCount = 0;
        persistence.GetByIdAsync(workItem.Id, ct).Returns(_ => BuildWorkItem("G1 1AA"));
        persistence.When(p => p.ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()))
            .Do(_ =>
            {
                callCount++;
                if (callCount < 2)
                {
                    throw new WorkItemConcurrencyException(workItem.Id, 0);
                }
            });

        var sut = BuildSut(persistence);
        await sut.OnSubmittedAsync(workItem, s_user, ct);

        await persistence.Received(2).ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnSubmittedAsync_abandons_after_max_retries()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem("G1 1AA");
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.GetByIdAsync(workItem.Id, ct).Returns(_ => BuildWorkItem("G1 1AA"));
        persistence.ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new WorkItemConcurrencyException(workItem.Id, 0));

        var sut = BuildSut(persistence);
        // Must not throw despite repeated concurrency failures.
        await sut.OnSubmittedAsync(workItem, s_user, ct);

        // 3 attempts before giving up.
        await persistence.Received(3).ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    // ─────────────────────────── OnActionAppliedAsync ───────────────────────

    [Fact]
    public async Task OnActionAppliedAsync_is_a_no_op()
    {
        var ct = TestContext.Current.CancellationToken;
        var persistence = Substitute.For<IWorkItemPersistence>();
        var workItem = BuildWorkItem();

        var sut = BuildSut(persistence);
        await sut.OnActionAppliedAsync(workItem, "approve", "submitted", s_user, ct);

        await persistence.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
