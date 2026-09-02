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
    private static readonly ClaimsPrincipal s_user = new(
        new ClaimsIdentity(
            [new Claim("user:id", "user-1"), new Claim("user:name", "Alice")],
            "test"
        )
    );

    private static readonly DateTime s_now = new(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static WorkItem BuildWorkItem(string? nation = "Scotland")
    {
        var payload = new BsonDocument { ["organisationName"] = "Acme Ltd" };
        if (nation is not null)
        {
            payload[ReAccreditationNationRoutingHook.NationKey] = nation;
        }

        return new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            Payload = payload,
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType()),
            TemplateVersion = "v3",
        };
    }

    private static ReAccreditationNationRoutingHook BuildSut(
        IWorkItemPersistence persistence,
        FakeTimeProvider? clock = null,
        TimeSpan? retryDelay = null
    )
    {
        clock ??= new FakeTimeProvider(s_now);
        // TimeSpan.Zero by default to keep retry tests fast and deterministic.
        retryDelay ??= TimeSpan.Zero;
        return new ReAccreditationNationRoutingHook(
            persistence,
            NullLogger<ReAccreditationNationRoutingHook>.Instance,
            clock,
            retryDelay
        );
    }

    [Fact]
    public void Constructor_defaults_time_provider_and_retry_delay_when_omitted()
    {
        // Covers the `timeProvider ?? TimeProvider.System` and
        // `retryDelay ?? TimeSpan.FromMilliseconds(50)` branches, which
        // BuildSut above never exercises because it always supplies both
        // explicitly. Both assignments run at construction.
        var hook = new ReAccreditationNationRoutingHook(
            Substitute.For<IWorkItemPersistence>(),
            NullLogger<ReAccreditationNationRoutingHook>.Instance
        );

        Assert.NotNull(hook);
    }

    // ─────────────────────────── OnSubmittedAsync ───────────────────────────

    [Theory]
    [InlineData("Scotland")]
    [InlineData("Wales")]
    [InlineData("NorthernIreland")]
    [InlineData("England")]
    [InlineData("scotland")]
    public async Task OnSubmittedAsync_uses_submitted_nation_and_appends_audit_entry(
        string submittedNation
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem(submittedNation);
        var freshCopy = BuildWorkItem(submittedNation);
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.GetByIdAsync(workItem.Id, ct).Returns(freshCopy);

        var sut = BuildSut(persistence);
        await sut.OnSubmittedAsync(workItem, s_user, ct);

        var expectedNation = Enum.Parse<Nation>(submittedNation, ignoreCase: true).ToString();

        await persistence
            .Received(1)
            .ReplaceAsync(
                Arg.Is<WorkItem>(w => w.Payload["nation"].AsString == expectedNation),
                ct
            );

        var entry = freshCopy.AuditLog.Single();
        Assert.Equal("routed-to-nation", entry.Action);
        Assert.Equal(expectedNation, entry.Details["nation"]);
        Assert.Equal("submitted", entry.Details["derivedFrom"]);
        // System-derived, not the submitting user's doing (RA-125 follow-up):
        // the entry must not be attributed to whoever happened to submit.
        Assert.Null(entry.CreatedBy);
        Assert.Null(entry.CreatedByName);
        Assert.Equal(s_now, entry.CreatedAt);
        // epr-rr9s: the routed-to-nation entry now snapshots the work item's
        // state at routing time (the post-submission initial state) so the
        // history UI can render it against its own state, not the live one.
        Assert.Equal("submitted", entry.StateId);
    }

    [Fact]
    public async Task OnSubmittedAsync_defaults_to_England_when_nation_absent()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem(nation: null);
        var freshCopy = BuildWorkItem(nation: null);
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.GetByIdAsync(workItem.Id, ct).Returns(freshCopy);

        var sut = BuildSut(persistence);
        await sut.OnSubmittedAsync(workItem, s_user, ct);

        await persistence
            .Received(1)
            .ReplaceAsync(Arg.Is<WorkItem>(w => w.Payload["nation"].AsString == "England"), ct);

        var entry = freshCopy.AuditLog.Single();
        Assert.Equal("default-england", entry.Details["derivedFrom"]);
    }

    [Fact]
    public async Task OnSubmittedAsync_defaults_to_England_when_submitted_nation_is_unrecognised()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem("Atlantis");
        var freshCopy = BuildWorkItem("Atlantis");
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.GetByIdAsync(workItem.Id, ct).Returns(freshCopy);

        var sut = BuildSut(persistence);
        await sut.OnSubmittedAsync(workItem, s_user, ct);

        await persistence
            .Received(1)
            .ReplaceAsync(Arg.Is<WorkItem>(w => w.Payload["nation"].AsString == "England"), ct);

        var entry = freshCopy.AuditLog.Single();
        Assert.Equal("default-england", entry.Details["derivedFrom"]);
    }

    [Fact]
    public async Task OnSubmittedAsync_defaults_to_England_when_submitted_nation_is_bson_null()
    {
        // Covers ResolveNation's `element.IsString` false arm for a BSON-null value.
        var ct = TestContext.Current.CancellationToken;
        var workItem = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            Payload = new BsonDocument
            {
                ["organisationName"] = "Acme Ltd",
                [ReAccreditationNationRoutingHook.NationKey] = BsonNull.Value,
            },
        };
        var freshCopy = new WorkItem
        {
            Id = workItem.Id,
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            Payload = new BsonDocument(workItem.Payload),
        };
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.GetByIdAsync(workItem.Id, ct).Returns(freshCopy);

        var sut = BuildSut(persistence);
        await sut.OnSubmittedAsync(workItem, s_user, ct);

        await persistence
            .Received(1)
            .ReplaceAsync(Arg.Is<WorkItem>(w => w.Payload["nation"].AsString == "England"), ct);
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
            TemplateVersion = "v3",
        };

        var sut = BuildSut(persistence);
        await sut.OnSubmittedAsync(workItem, s_user, ct);

        await persistence
            .DidNotReceive()
            .GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
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

        await persistence
            .DidNotReceive()
            .ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnSubmittedAsync_retries_on_concurrency_exception_and_succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem("Scotland");
        var persistence = Substitute.For<IWorkItemPersistence>();

        var callCount = 0;
        persistence.GetByIdAsync(workItem.Id, ct).Returns(_ => BuildWorkItem("Scotland"));
        persistence
            .When(p => p.ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()))
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

        await persistence
            .Received(2)
            .ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnSubmittedAsync_waits_out_a_jittered_backoff_before_retrying()
    {
        // Covers the `_retryDelay > TimeSpan.Zero` true arm — every other
        // retry test uses BuildSut's TimeSpan.Zero default to stay fast.
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem("Scotland");
        var persistence = Substitute.For<IWorkItemPersistence>();

        var callCount = 0;
        persistence.GetByIdAsync(workItem.Id, ct).Returns(_ => BuildWorkItem("Scotland"));
        persistence
            .When(p => p.ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()))
            .Do(_ =>
            {
                callCount++;
                if (callCount < 2)
                {
                    throw new WorkItemConcurrencyException(workItem.Id, 0);
                }
            });

        var sut = BuildSut(persistence, retryDelay: TimeSpan.FromMilliseconds(5));
        await sut.OnSubmittedAsync(workItem, s_user, ct);

        await persistence
            .Received(2)
            .ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnSubmittedAsync_abandons_after_max_retries()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem("Scotland");
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.GetByIdAsync(workItem.Id, ct).Returns(_ => BuildWorkItem("Scotland"));
        persistence
            .ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new WorkItemConcurrencyException(workItem.Id, 0));

        var sut = BuildSut(persistence);
        // Must not throw despite repeated concurrency failures.
        await sut.OnSubmittedAsync(workItem, s_user, ct);

        // 3 attempts before giving up.
        await persistence
            .Received(3)
            .ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
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

        await persistence
            .DidNotReceive()
            .GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
