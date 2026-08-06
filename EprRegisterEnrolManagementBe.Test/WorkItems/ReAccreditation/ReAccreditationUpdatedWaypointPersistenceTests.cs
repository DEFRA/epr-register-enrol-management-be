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
/// RA-372 regression cover for the duly-making waypoint discharge, run against
/// a real ephemeral MongoDB with the real <see cref="WorkItemPersistence"/> and
/// the real <see cref="WorkItemAuditAppender"/>.
///
/// This suite exists because an in-process test cannot see the defect it
/// guards. The first attempt at the discharge persisted it as its own step and
/// then saved again for the duly-made transition. Between those two saves the
/// status push writes an audit entry, and
/// <see cref="WorkItemAuditAppender"/> does that by re-reading and replacing
/// the whole document — which moves <see cref="WorkItem.Version"/> on and makes
/// the second save fail its optimistic-concurrency check. In production that
/// surfaced as HTTP 500 with the application stranded in <c>submitted</c>,
/// every task complete and no way forward. Against a substituted
/// <see cref="IWorkItemPersistence"/> there is no version protocol and no
/// out-of-band write, so the same code passed.
///
/// The rule these tests pin: everything the hook mutates lands in ONE save,
/// and the save happens before any push.
/// </summary>
public class ReAccreditationUpdatedWaypointPersistenceTests
    : IClassFixture<MongoIntegrationFixture>,
        IAsyncDisposable
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
    /// queried during duly-making, operator responds, regulator finishes the
    /// checklist while the item sits in <c>updated</c>.
    /// </summary>
    [Fact]
    public async Task Completing_the_last_duly_making_task_while_updated_reaches_duly_made()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = await SeedUpdatedWorkItemAsync(ct);
        var pushes = new List<(string ActionId, string FromStateId)>();
        var engine = BuildEngine(pushes);

        // The defect surfaced here as an unhandled WorkItemConcurrencyException
        // that the engine's hook fan-out rethrows, so the request 500s.
        var result = await engine.CompleteTaskAsync(
            workItem.Id,
            "confirm-application-completeness",
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

        // Both tasks are complete under the originating state, and the whole
        // declared path is on the record.
        Assert.Equal(
            ["confirm-application-completeness", "verify-organisation-details"],
            stored.CompletedTaskIdsByState["submitted"].OrderBy(t => t, StringComparer.Ordinal)
        );

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

        // The status push landed, and its audit entry survived — proof the
        // out-of-band append and the hook's own save did not clobber one
        // another.
        Assert.Equal([("duly-make", "submitted")], pushes);
        Assert.Contains(stored.AuditLog, e => e.Action.StartsWith("status-push-"));
        Assert.Contains(stored.AuditLog, e => e.Action == "sla-clock-started");
    }

    /// <summary>
    /// The ordinary duly-making journey — no query, no waypoint — must be
    /// unchanged by the discharge logic.
    /// </summary>
    [Fact]
    public async Task Completing_the_last_duly_making_task_from_submitted_is_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = await SeedUpdatedWorkItemAsync(ct, stateId: "submitted", withResume: false);
        var pushes = new List<(string ActionId, string FromStateId)>();
        var engine = BuildEngine(pushes);

        var result = await engine.CompleteTaskAsync(
            workItem.Id,
            "confirm-application-completeness",
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

    private async Task<WorkItem> SeedUpdatedWorkItemAsync(
        CancellationToken cancellationToken,
        string stateId = "updated",
        bool withResume = true
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
                        ["actionId"] = "resume-during-duly-making",
                        ["actionDisplayName"] = "Resume",
                        ["fromStateId"] = "queried",
                        ["toStateId"] = "updated",
                    },
                }
            );
        }

        // One duly-making task already ticked, so completing the second is the
        // one that triggers the hook.
        workItem.CompletedTaskIdsByState["submitted"] = new(
            ["verify-organisation-details"],
            StringComparer.OrdinalIgnoreCase
        );
        workItem.TaskStatusesByState["submitted"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["verify-organisation-details"] = WorkItemTaskStatus.Completed,
        };

        await _persistence.CreateAsync(workItem, cancellationToken);
        return workItem;
    }

    /// <summary>
    /// Everything real except the two outbound integrations (Notify and the
    /// operator-backend push adapter). In particular the audit appender is the
    /// genuine <see cref="WorkItemAuditAppender"/>, because its re-read-and-
    /// replace is the write that broke the hook's optimistic concurrency.
    /// </summary>
    private WorkItemService BuildEngine(List<(string ActionId, string FromStateId)> pushes)
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

        var dulyMadeHook = new ReAccreditationDulyMadeHook(
            _persistence,
            notifyClient,
            auditAppender,
            new ReAccreditationStatusPushHook(
                pushAdapter,
                auditAppender,
                NullLogger<ReAccreditationStatusPushHook>.Instance
            ),
            TimeProvider.System,
            NullLogger<ReAccreditationDulyMadeHook>.Instance
        );

        return new WorkItemService(
            new WorkItemRegistry([new ReAccreditationType()]),
            _persistence,
            NullLogger<WorkItemService>.Instance,
            postTaskHooks: [dulyMadeHook],
            taskStateResolvers: [new ReAccreditationTaskStateResolver()]
        );
    }
}
