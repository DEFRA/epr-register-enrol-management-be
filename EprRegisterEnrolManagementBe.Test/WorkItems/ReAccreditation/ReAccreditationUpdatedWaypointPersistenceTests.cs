using System.Security.Claims;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Notifications;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-372 / RA-316 regression cover for the duly-making waypoint discharge, run
/// against a real ephemeral MongoDB with the real
/// <see cref="WorkItemPersistence"/> and the real
/// <see cref="WorkItemAuditAppender"/>.
///
/// This suite exists because an in-process test cannot see the defect it
/// guards. The first attempt at the discharge persisted it as its own step and
/// then saved again for the duly-made transition. Between those two saves the
/// status push writes an audit entry, and
/// <see cref="WorkItemAuditAppender"/> does that by re-reading and replacing the
/// whole document — which moves <see cref="WorkItem.Version"/> on and makes the
/// second save fail its optimistic-concurrency check. In production that
/// surfaced as HTTP 500 with the application stranded, no way forward. Against a
/// substituted <see cref="IWorkItemPersistence"/> there is no version protocol
/// and no out-of-band write, so the same code passed.
///
/// The rule these tests pin: everything the duly-making service mutates lands in
/// ONE save, and the save happens before any push.
///
/// RA-316 moved this contract from the deleted <c>ReAccreditationDulyMadeHook</c>
/// to <see cref="ReAccreditationDulyMakingService"/>. The trigger changed from
/// "the last submitted-state task was ticked" to "the regulator pressed Duly
/// make and gave a payment date", but the persistence hazard is identical and so
/// is the declared path through the state machine.
/// </summary>
public class ReAccreditationUpdatedWaypointPersistenceTests
    : IAsyncDisposable
{
    private static readonly ClaimsPrincipal s_user = new(
        new ClaimsIdentity(
            [
                new Claim("cognito:client_id", "test-client"),
                new Claim("user:id", "alice-1"),
                new Claim("user:name", "Alice Example"),
            ],
            "test"
        )
    );

    private static readonly DateOnly s_paymentDate = new(2026, 7, 15);

    private readonly TestMongoDbClientFactory _clientFactory;
    private readonly string _databaseName;
    private readonly WorkItemPersistence _persistence;

    public ReAccreditationUpdatedWaypointPersistenceTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("waypoint");
        _clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _persistence = new WorkItemPersistence(_clientFactory, NullLoggerFactory.Instance);
    }

    public async ValueTask DisposeAsync() =>
        await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);

    /// <summary>
    /// The full journey RA-372 is about, end to end through real persistence:
    /// queried during duly-making, operator responds, regulator completes duly
    /// making while the item still sits in <c>updated</c>.
    ///
    /// The lead flagged this as a real production path that must keep working
    /// even though management-fe is shipping the call to action for
    /// <c>submitted</c> only in RA-316, so it is pinned here at the API level.
    /// </summary>
    [Fact]
    public async Task Completing_duly_making_while_updated_reaches_duly_made()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = await SeedWorkItemAsync(ct);
        var pushes = new List<(string ActionId, string FromStateId)>();
        var service = BuildService(pushes);

        // The defect surfaced here as an unhandled WorkItemConcurrencyException
        // bubbling out as a 500.
        var result = await service.CompleteDulyMakingAsync(
            workItem.Id,
            s_paymentDate,
            s_user,
            ct
        );

        Assert.True(result.IsSuccess);

        // Assert against the document read back from Mongo, not the in-memory
        // instance — a save that never landed would still look right in memory.
        var stored = await _persistence.GetByIdAsync(workItem.Id, ct);
        Assert.NotNull(stored);
        Assert.Equal("duly-made", stored!.StateId);
        Assert.NotNull(stored.SlaClock);

        // AC06: anchored to the entered payment date, not to now.
        Assert.Equal(
            s_paymentDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            stored.SlaClock!.StartedAt
        );

        // The whole declared path is on the record, and the undeclared
        // updated → duly-made shortcut is not.
        var applied = AppliedTransitions(stored);
        Assert.Equal(
            [
                ("resume-during-duly-making", "queried", "updated"),
                ("continue-review-during-duly-making", "updated", "submitted"),
                ("duly-make", "submitted", "duly-made"),
            ],
            applied
        );
        Assert.DoesNotContain(applied, e => e.From == "updated" && e.To == "duly-made");

        // The status push landed with the modelled from-state, and its audit
        // entry survived — proof the out-of-band append and the service's own
        // save did not clobber one another.
        Assert.Equal([("duly-make", "submitted")], pushes);
        Assert.Contains(stored.AuditLog, e => e.Action.StartsWith("status-push-"));
        Assert.Contains(stored.AuditLog, e => e.Action == "sla-clock-started");
    }

    /// <summary>
    /// The ordinary duly-making journey — no query, no waypoint — must be
    /// unchanged by the discharge logic.
    /// </summary>
    [Fact]
    public async Task Completing_duly_making_from_submitted_is_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = await SeedWorkItemAsync(ct, stateId: "submitted", withResume: false);
        var pushes = new List<(string ActionId, string FromStateId)>();
        var service = BuildService(pushes);

        var result = await service.CompleteDulyMakingAsync(
            workItem.Id,
            s_paymentDate,
            s_user,
            ct
        );

        Assert.True(result.IsSuccess);

        var stored = await _persistence.GetByIdAsync(workItem.Id, ct);
        Assert.Equal("duly-made", stored!.StateId);

        // No waypoint to discharge, so no continue-review entry is invented.
        Assert.Equal([("duly-make", "submitted", "duly-made")], AppliedTransitions(stored));
        Assert.Equal([("duly-make", "submitted")], pushes);
    }

    /// <summary>
    /// An item in <c>updated</c> whose query was raised somewhere OTHER than
    /// duly-making is mid-review. Duly making it would skip whole stages, so it
    /// is refused — and refused without writing anything.
    /// </summary>
    [Fact]
    public async Task An_updated_item_queried_from_assessment_cannot_be_duly_made()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = await SeedWorkItemAsync(ct, resumeActionId: "resume-during-assessment");
        var pushes = new List<(string ActionId, string FromStateId)>();
        var service = BuildService(pushes);

        var result = await service.CompleteDulyMakingAsync(
            workItem.Id,
            s_paymentDate,
            s_user,
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);

        var stored = await _persistence.GetByIdAsync(workItem.Id, ct);
        Assert.Equal("updated", stored!.StateId);
        Assert.Null(stored.SlaClock);
        Assert.Empty(pushes);
        // A refusal writes no audit entry — only the seeded resume survives.
        Assert.Single(AppliedTransitions(stored));
    }

    private static List<(string? ActionId, string? From, string? To)> AppliedTransitions(
        WorkItem workItem
    ) =>
        workItem
            .AuditLog.Where(e => e.Action == "action-applied")
            .Select(e =>
                (
                    e.Details.GetValueOrDefault("actionId"),
                    e.Details.GetValueOrDefault("fromStateId"),
                    e.Details.GetValueOrDefault("toStateId")
                )
            )
            .ToList();

    private async Task<WorkItem> SeedWorkItemAsync(
        CancellationToken cancellationToken,
        string stateId = "updated",
        bool withResume = true,
        string resumeActionId = "resume-during-duly-making"
    )
    {
        var workItem = new WorkItem
        {
            Id = Guid.NewGuid(),
            TypeId = ReAccreditationType.Id,
            StateId = stateId,
            SubmittedAt = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc),
            LastModifiedAt = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc),
            SubmittedBy = "test-client",
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType()),
            Payload = new BsonDocument
            {
                ["organisationName"] = "Acme Ltd",
                ["operatorEmail"] = "op@example.com",
                ["applicationReference"] = "AP26EAABCDE1AB",
            },
        };

        if (withResume)
        {
            workItem.AuditLog.Add(
                new WorkItemAuditEntry
                {
                    Action = "action-applied",
                    ActionDisplayName = "Action applied",
                    CreatedAt = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc),
                    Details = new Dictionary<string, string?>
                    {
                        ["actionId"] = resumeActionId,
                        ["actionDisplayName"] = "Resume",
                        ["fromStateId"] = "queried",
                        ["toStateId"] = "updated",
                    },
                }
            );
        }

        await _persistence.CreateAsync(workItem, cancellationToken);
        return workItem;
    }

    /// <summary>
    /// Everything real except the two outbound integrations (Notify and the
    /// operator-backend push adapter). In particular the audit appender is the
    /// genuine <see cref="WorkItemAuditAppender"/>, because its
    /// re-read-and-replace is the write that broke optimistic concurrency.
    /// </summary>
    private ReAccreditationDulyMakingService BuildService(
        List<(string ActionId, string FromStateId)> pushes
    )
    {
        var auditAppender = new WorkItemAuditAppender(
            _persistence,
            NullLogger<WorkItemAuditAppender>.Instance
        );

        var notifyClient = Substitute.For<INotifyClient>();
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg-id"));

        var pushAdapter = Substitute.For<IOperatorBackendPushAdapter>();
        pushAdapter
            .PushStatusChangedAsync(
                Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call =>
            {
                pushes.Add((call.ArgAt<string>(5), call.ArgAt<string>(2)));
                // Skipped is the production default when the push is disabled,
                // and it still writes a status-push-skipped audit entry — the
                // out-of-band write this suite exists to keep honest.
                return OperatorBackendPushResult.Skipped("disabled in test");
            });

        var notificationHook = new ReAccreditationNotificationHook(
            notifyClient,
            auditAppender,
            Substitute.For<IRegulatorMailboxResolver>(),
            _persistence,
            NullLogger<ReAccreditationNotificationHook>.Instance
        );

        var statusPushHook = new ReAccreditationStatusPushHook(
            pushAdapter,
            auditAppender,
            NullLogger<ReAccreditationStatusPushHook>.Instance
        );

        return new ReAccreditationDulyMakingService(
            _persistence,
            new WorkItemRegistry([new ReAccreditationType()]),
            [notificationHook, statusPushHook],
            TimeProvider.System,
            NullLogger<ReAccreditationDulyMakingService>.Instance
        );
    }
}
