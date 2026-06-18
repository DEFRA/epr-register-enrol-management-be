using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.Collections.Generic;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// Unit tests for <see cref="ArchiveBackgroundService.RunOnceAsync"/>.
/// Persistence is substituted; <see cref="FakeTimeProvider"/> controls "now".
/// </summary>
public class ArchiveBackgroundServiceTests
{
    private static readonly DateTimeOffset s_fixedNow =
        new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    // Registry whose single type declares the three terminal states the archive
    // job must now treat uniformly (RA-224).
    private static IWorkItemRegistry TerminalRegistry() =>
        new WorkItemRegistry(new IWorkItemType[]
        {
            new TestWorkItemType(
                "re-accreditation",
                "Re-accreditation",
                states: new[]
                {
                    new WorkItemState("submitted", "Submitted"),
                    new WorkItemState("approved", "Approved", IsTerminal: true),
                    new WorkItemState("rejected", "Rejected", IsTerminal: true),
                    new WorkItemState("withdrawn", "Withdrawn", IsTerminal: true)
                })
        });

    /// <summary>
    /// Builds a work item sitting in a terminal <paramref name="stateId"/>
    /// (defaults to <c>approved</c>).
    /// <paramref name="enteredDaysAgo"/> sets both the audit-log
    /// "entered terminal state" timestamp and <c>LastModifiedAt</c> (the fallback
    /// path). Pass <paramref name="lastModifiedDaysAgo"/> to simulate a
    /// post-decision write (note, assignment, SLA stamp) that bumps
    /// <c>LastModifiedAt</c> without changing the actual decision time.
    /// </summary>
    private static WorkItem TerminalItem(
        int enteredDaysAgo,
        string stateId = "approved",
        bool alreadyArchived = false,
        int? lastModifiedDaysAgo = null,
        bool withAuditEntry = true)
    {
        var enteredAt = s_fixedNow.AddDays(-enteredDaysAgo).UtcDateTime;
        var lastModified = lastModifiedDaysAgo.HasValue
            ? s_fixedNow.AddDays(-lastModifiedDaysAgo.Value).UtcDateTime
            : enteredAt;

        var payload = new BsonDocument();
        if (alreadyArchived)
        {
            payload[ArchiveBackgroundService.ArchivedAtPayloadKey] = new BsonDateTime(enteredAt);
        }

        var item = new WorkItem
        {
            TypeId = "re-accreditation",
            StateId = stateId,
            SubmittedBy = "test-client",
            Payload = payload,
            LastModifiedAt = lastModified,
            SubmittedAt = enteredAt
        };

        if (withAuditEntry)
        {
            // The action-applied entry the service uses to derive when the item
            // entered its terminal state (toStateId == current StateId).
            item.AuditLog.Add(new WorkItemAuditEntry
            {
                Action = "action-applied",
                ActionDisplayName = "Action applied",
                CreatedAt = enteredAt,
                Details = new Dictionary<string, string?> { ["toStateId"] = stateId }
            });
        }

        return item;
    }

    // Back-compat shim for the original approved-only tests.
    private static WorkItem ApprovedItem(
        int approvedDaysAgo,
        bool alreadyArchived = false,
        int? lastModifiedDaysAgo = null) =>
        TerminalItem(approvedDaysAgo, "approved", alreadyArchived, lastModifiedDaysAgo);

    private sealed record Sut(
        ArchiveBackgroundService Service,
        IWorkItemPersistence Persistence);

    private static Sut Build(IEnumerable<WorkItem>? pageItems = null, DateTimeOffset? now = null)
    {
        var persistence = Substitute.For<IWorkItemPersistence>();
        var time = new FakeTimeProvider(now ?? s_fixedNow);
        var config = new ConfigurationBuilder().Build();

        var items = (pageItems ?? []).ToList();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage(items, items.Count, 1, items.Count));

        foreach (var item in items)
        {
            persistence.GetByIdAsync(item.Id, Arg.Any<CancellationToken>())
                .Returns(item);
        }

        var services = new ServiceCollection();
        services.AddSingleton(persistence);
        var provider = services.BuildServiceProvider();

        var service = new ArchiveBackgroundService(
            provider, time,
            NullLogger<ArchiveBackgroundService>.Instance,
            config,
            TerminalRegistry());

        return new Sut(service, persistence);
    }

    [Fact]
    public async Task RunOnceAsync_stamps_archivedAt_on_old_approved_item()
    {
        // Must be strictly MORE than ArchiveAfterDays old to qualify.
        var item = ApprovedItem(approvedDaysAgo: ArchiveBackgroundService.ArchiveAfterDays + 1);
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(item.Payload.Contains(ArchiveBackgroundService.ArchivedAtPayloadKey));
        await sut.Persistence.Received(1).ReplaceAsync(item, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnceAsync_skips_item_not_yet_old_enough()
    {
        var item = ApprovedItem(approvedDaysAgo: ArchiveBackgroundService.ArchiveAfterDays - 1);
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.False(item.Payload.Contains(ArchiveBackgroundService.ArchivedAtPayloadKey));
        await sut.Persistence.DidNotReceive().ReplaceAsync(item, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnceAsync_skips_already_archived_item()
    {
        var item = ApprovedItem(approvedDaysAgo: ArchiveBackgroundService.ArchiveAfterDays + 1, alreadyArchived: true);
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        await sut.Persistence.DidNotReceive().ReplaceAsync(item, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnceAsync_queries_all_terminal_states_with_include_archived()
    {
        var sut = Build();

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        await sut.Persistence.Received(1).QueryAsync(
            Arg.Is<WorkItemQuery>(q =>
                q.StateIds != null &&
                q.StateIds.Contains("approved") &&
                q.StateIds.Contains("rejected") &&
                q.StateIds.Contains("withdrawn") &&
                q.IncludeArchived),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnceAsync_returns_early_when_no_terminal_states_registered()
    {
        var persistence = Substitute.For<IWorkItemPersistence>();
        var services = new ServiceCollection();
        services.AddSingleton(persistence);

        var service = new ArchiveBackgroundService(
            services.BuildServiceProvider(),
            new FakeTimeProvider(s_fixedNow),
            NullLogger<ArchiveBackgroundService>.Instance,
            new ConfigurationBuilder().Build(),
            new WorkItemRegistry([]));

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        // With no terminal states the job must not query at all — an empty
        // StateIds set would match every item rather than none.
        await persistence.DidNotReceive().QueryAsync(
            Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("withdrawn")]
    public async Task RunOnceAsync_stamps_archivedAt_on_old_item_in_any_terminal_state(string stateId)
    {
        var item = TerminalItem(
            enteredDaysAgo: ArchiveBackgroundService.ArchiveAfterDays + 1,
            stateId: stateId);
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(item.Payload.Contains(ArchiveBackgroundService.ArchivedAtPayloadKey));
        await sut.Persistence.Received(1).ReplaceAsync(item, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("withdrawn")]
    public async Task RunOnceAsync_skips_recent_item_in_any_terminal_state(string stateId)
    {
        var item = TerminalItem(
            enteredDaysAgo: ArchiveBackgroundService.ArchiveAfterDays - 1,
            stateId: stateId);
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.False(item.Payload.Contains(ArchiveBackgroundService.ArchivedAtPayloadKey));
        await sut.Persistence.DidNotReceive().ReplaceAsync(item, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnceAsync_audit_entry_records_state_neutral_keys()
    {
        var item = TerminalItem(
            enteredDaysAgo: ArchiveBackgroundService.ArchiveAfterDays + 1,
            stateId: "rejected");
        var enteredAt = item.AuditLog[0].CreatedAt;
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        var entry = item.AuditLog.Single(e => e.Action == "archived");
        Assert.Equal("Archived", entry.ActionDisplayName);
        Assert.Equal(s_fixedNow.UtcDateTime, entry.CreatedAt);
        Assert.Equal(enteredAt.ToString("O"), entry.Details["enteredStateAt"]);
        Assert.Equal(s_fixedNow.UtcDateTime.ToString("O"), entry.Details["archivedAt"]);
        // The legacy approved-only key must be gone.
        Assert.False(entry.Details.ContainsKey("approvedAt"));
    }

    [Fact]
    public async Task RunOnceAsync_uses_audit_entry_matching_current_terminal_state()
    {
        // An item rejected 8 days ago whose ONLY matching audit entry is the
        // rejection. A note bumped LastModifiedAt to 2 days ago. The service
        // must use the rejection audit time (eligible), not LastModifiedAt.
        var item = TerminalItem(
            enteredDaysAgo: ArchiveBackgroundService.ArchiveAfterDays + 1,
            stateId: "withdrawn",
            lastModifiedDaysAgo: 2);
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(item.Payload.Contains(ArchiveBackgroundService.ArchivedAtPayloadKey));
    }

    [Fact]
    public async Task RunOnceAsync_handles_empty_page_without_error()
    {
        var sut = Build([]);

        var ex = await Record.ExceptionAsync(
            () => sut.Service.RunOnceAsync(TestContext.Current.CancellationToken));

        Assert.Null(ex);
    }

    [Fact]
    public async Task RunOnceAsync_item_exactly_at_threshold_is_skipped()
    {
        // LastModifiedAt == now - ArchiveAfterDays (not strictly older).
        var item = ApprovedItem(approvedDaysAgo: ArchiveBackgroundService.ArchiveAfterDays);
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        await sut.Persistence.DidNotReceive().ReplaceAsync(item, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnceAsync_concurrency_exception_on_replace_is_swallowed()
    {
        var item = ApprovedItem(approvedDaysAgo: ArchiveBackgroundService.ArchiveAfterDays + 1);
        var sut = Build([item]);

        sut.Persistence
            .ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .Throws(new WorkItemConcurrencyException(item.Id, 0));

        var ex = await Record.ExceptionAsync(
            () => sut.Service.RunOnceAsync(TestContext.Current.CancellationToken));

        Assert.Null(ex);
        await sut.Persistence.Received(1).ReplaceAsync(item, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnceAsync_get_by_id_returns_null_skips_replace()
    {
        // Simulates the item being deleted between the list query and the full-load.
        var item = ApprovedItem(approvedDaysAgo: ArchiveBackgroundService.ArchiveAfterDays + 1);
        var sut = Build([item]);

        sut.Persistence
            .GetByIdAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        await sut.Persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnceAsync_stamps_last_modified_at_to_now()
    {
        var item = ApprovedItem(approvedDaysAgo: ArchiveBackgroundService.ArchiveAfterDays + 1);
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(s_fixedNow.UtcDateTime, item.LastModifiedAt);
    }

    [Fact]
    public async Task RunOnceAsync_uses_audit_log_approval_time_not_last_modified()
    {
        // Approved 8 days ago (eligible), but a note bumped LastModifiedAt to
        // 2 days ago. Without the audit-log fix the job would skip this item.
        var item = ApprovedItem(
            approvedDaysAgo: ArchiveBackgroundService.ArchiveAfterDays + 1,
            lastModifiedDaysAgo: 2);
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(item.Payload.Contains(ArchiveBackgroundService.ArchivedAtPayloadKey));
        await sut.Persistence.Received(1).ReplaceAsync(item, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnceAsync_item_not_yet_old_enough_per_audit_log_is_skipped_despite_old_last_modified()
    {
        // LastModifiedAt is 8 days old but the audit-log approval entry is only
        // 6 days old — the item is not yet eligible.
        var item = ApprovedItem(
            approvedDaysAgo: ArchiveBackgroundService.ArchiveAfterDays - 1,
            lastModifiedDaysAgo: ArchiveBackgroundService.ArchiveAfterDays + 1);
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        await sut.Persistence.DidNotReceive().ReplaceAsync(item, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnceAsync_paginates_until_partial_page()
    {
        // Use a batch size of 2 (via config) so we can test multi-page
        // behaviour without creating MaxPageSize items.
        var item1 = ApprovedItem(approvedDaysAgo: ArchiveBackgroundService.ArchiveAfterDays + 1);
        var item2 = ApprovedItem(approvedDaysAgo: ArchiveBackgroundService.ArchiveAfterDays + 1);
        var item3 = ApprovedItem(approvedDaysAgo: ArchiveBackgroundService.ArchiveAfterDays + 1);

        var persistence = Substitute.For<IWorkItemPersistence>();
        // Page 1: full batch — triggers fetch of page 2.
        persistence.QueryAsync(
                Arg.Is<WorkItemQuery>(q => q.Page == 1), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage([item1, item2], 3, 1, 2));
        // Page 2: partial batch — stops pagination.
        persistence.QueryAsync(
                Arg.Is<WorkItemQuery>(q => q.Page == 2), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage([item3], 3, 2, 2));

        foreach (var item in new[] { item1, item2, item3 })
            persistence.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ArchiveJob:BatchSize"] = "2" })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton(persistence);
        var service = new ArchiveBackgroundService(
            services.BuildServiceProvider(),
            new FakeTimeProvider(s_fixedNow),
            NullLogger<ArchiveBackgroundService>.Instance,
            config,
            TerminalRegistry());

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        // Both pages must be fetched and all three items stamped.
        await persistence.Received(2).QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>());
        await persistence.Received(3).ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnceAsync_stops_after_single_page_when_page_is_partial()
    {
        // A partial page on the first fetch means there is no page 2.
        var item = ApprovedItem(approvedDaysAgo: ArchiveBackgroundService.ArchiveAfterDays + 1);
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        // QueryAsync called exactly once (no second page fetched).
        await sut.Persistence.Received(1).QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnceAsync_falls_back_to_last_modified_when_no_audit_entry()
    {
        // Item has no action-applied audit entry (pre-dates audit logging).
        // The service must fall back to LastModifiedAt.
        var item = new WorkItem
        {
            TypeId = "re-accreditation",
            StateId = "approved",
            SubmittedBy = "test-client",
            Payload = new BsonDocument(),
            LastModifiedAt = s_fixedNow.AddDays(-(ArchiveBackgroundService.ArchiveAfterDays + 1)).UtcDateTime,
            SubmittedAt = s_fixedNow.AddDays(-(ArchiveBackgroundService.ArchiveAfterDays + 1)).UtcDateTime
        };
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(item.Payload.Contains(ArchiveBackgroundService.ArchivedAtPayloadKey));
        await sut.Persistence.Received(1).ReplaceAsync(item, Arg.Any<CancellationToken>());
    }
}
