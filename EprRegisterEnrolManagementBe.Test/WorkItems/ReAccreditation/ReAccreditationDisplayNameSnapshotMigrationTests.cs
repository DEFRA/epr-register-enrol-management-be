using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-324/AC06: renames four state display labels in every re-accreditation
/// work item's frozen snapshot to the "Applications" set and bumps the frozen
/// template version (v8 → v9). Mirrors
/// <see cref="ReAccreditationUpdatedStateSnapshotMigrationTests"/>'s structure.
/// </summary>
public class ReAccreditationDisplayNameSnapshotMigrationTests
{
    private static readonly IReadOnlyDictionary<string, string> s_oldLabels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["submitted"] = "Submitted",
            ["assessment-in-progress"] = "Assessment in progress",
            ["approved"] = "Approved",
            ["rejected"] = "Rejected",
        };

    /// <summary>
    /// A pre-migration v8 snapshot: the live (v9) structure, but with the four
    /// renamed states reverted to their old labels and the version set to v8.
    /// </summary>
    private static WorkItemTemplateSnapshot BuildV8Snapshot()
    {
        var snapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        return new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v8",
            States = snapshot.States
                .Select(s => s_oldLabels.TryGetValue(s.Id, out var old) ? s with { DisplayName = old } : s)
                .ToList(),
            Transitions = snapshot.Transitions
        };
    }

    private static WorkItem BuildItem(
        string stateId = "approved",
        WorkItemTemplateSnapshot? snapshot = null) =>
        new()
        {
            TypeId = ReAccreditationType.Id,
            StateId = stateId,
            TemplateSnapshot = snapshot ?? BuildV8Snapshot(),
            TemplateVersion = "v8",
            SubmittedAt = DateTime.UtcNow,
            LastModifiedAt = DateTime.UtcNow
        };

    private static WorkItemPage SinglePage(params WorkItem[] items) =>
        new(items, items.Length, 1, WorkItemQuery.MaxPageSize);

    private static ReAccreditationDisplayNameSnapshotMigration BuildSut() =>
        new(NullLogger<ReAccreditationDisplayNameSnapshotMigration>.Instance);

    [Theory]
    [InlineData("submitted", "Not started")]
    [InlineData("assessment-in-progress", "Updated")]
    [InlineData("approved", "Granted")]
    [InlineData("rejected", "Refused")]
    public async Task ApplyAsync_renames_the_ac06_state_labels(string stateId, string expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        var state = item.TemplateSnapshot!.States.Single(s => s.Id == stateId);
        Assert.Equal(expected, state.DisplayName);
    }

    [Theory]
    [InlineData("duly-made", "Duly made")]
    [InlineData("awaiting-decision", "Awaiting decision")]
    [InlineData("queried", "Queried")]
    [InlineData("updated", "Updated")]
    [InlineData("withdrawn", "Withdrawn")]
    public async Task ApplyAsync_leaves_other_state_labels_untouched(string stateId, string expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        var state = item.TemplateSnapshot!.States.Single(s => s.Id == stateId);
        Assert.Equal(expected, state.DisplayName);
    }

    [Fact]
    public async Task ApplyAsync_preserves_transitions_and_terminal_flags()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var originalTransitionCount = item.TemplateSnapshot!.Transitions.Count;
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Equal(originalTransitionCount, item.TemplateSnapshot!.Transitions.Count);
        // Relabelling must not disturb IsTerminal on the renamed terminal states.
        Assert.True(item.TemplateSnapshot!.States.Single(s => s.Id == "approved").IsTerminal);
        Assert.True(item.TemplateSnapshot!.States.Single(s => s.Id == "rejected").IsTerminal);
    }

    [Fact]
    public async Task ApplyAsync_bumps_template_version_to_v9()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Equal("v9", item.TemplateVersion);
        Assert.Equal("v9", item.TemplateSnapshot!.TemplateVersion);
    }

    [Fact]
    public async Task ApplyAsync_skips_items_already_on_v9_labels()
    {
        var ct = TestContext.Current.CancellationToken;
        var v9Snapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        var item = BuildItem(snapshot: v9Snapshot);
        item.TemplateVersion = "v9";

        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().GetByIdAsync(default, ct);
        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    [Fact]
    public async Task ApplyAsync_skips_items_with_no_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        item.TemplateSnapshot = null;

        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().GetByIdAsync(default, ct);
        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    [Fact]
    public async Task ApplyAsync_does_not_change_the_work_items_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(stateId: "approved");
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Equal("approved", item.StateId);
        Assert.Empty(item.AuditLog);
    }

    [Fact]
    public async Task ApplyAsync_saves_once_per_item_needing_migration()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.Received(1).ReplaceAsync(item, ct);
    }

    [Fact]
    public async Task ApplyAsync_saves_the_full_audit_bearing_document_not_the_query_candidate()
    {
        var ct = TestContext.Current.CancellationToken;

        // QueryAsync omits AuditLog/Notes, so the candidate arrives audit-stripped.
        var candidate = BuildItem();
        Assert.Empty(candidate.AuditLog);

        // GetByIdAsync returns the full document, distinct from the candidate,
        // carrying the audit history. Saving the candidate instead would wipe it.
        var full = BuildItem();
        full.AuditLog.Add(new WorkItemAuditEntry
        {
            Action = "submitted",
            ActionDisplayName = "Submitted",
            CreatedAt = DateTime.UtcNow,
            CreatedBy = "user-1",
            CreatedByName = "Alice"
        });

        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(candidate));
        persistence.GetByIdAsync(candidate.Id, ct).Returns(full);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.Received(1).ReplaceAsync(full, ct);
        await persistence.DidNotReceive().ReplaceAsync(candidate, ct);
        // The saved (full) document keeps its audit entries and gets relabelled.
        Assert.Single(full.AuditLog);
        Assert.Equal("Not started", full.TemplateSnapshot!.States.Single(s => s.Id == "submitted").DisplayName);
        Assert.Equal("v9", full.TemplateVersion);
    }

    [Fact]
    public async Task ApplyAsync_skips_when_full_document_is_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns((WorkItem?)null);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    [Fact]
    public async Task ApplyAsync_skips_when_full_document_was_migrated_concurrently()
    {
        var ct = TestContext.Current.CancellationToken;
        // Candidate carries the stale label (needs migration), but the full
        // re-read comes back already relabelled — a concurrent migration won.
        var candidate = BuildItem();
        var full = BuildItem(snapshot: WorkItemTemplateSnapshot.Capture(new ReAccreditationType()));
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(candidate));
        persistence.GetByIdAsync(candidate.Id, ct).Returns(full);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    [Fact]
    public async Task ApplyAsync_swallows_concurrency_exception_and_continues()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);
        persistence.ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new WorkItemConcurrencyException(item.Id, expectedVersion: 0)));

        // Should not throw
        await BuildSut().ApplyAsync(persistence, ct);
    }

    [Fact]
    public async Task ApplyAsync_pages_through_all_results()
    {
        var ct = TestContext.Current.CancellationToken;
        var item1 = BuildItem();
        var item2 = BuildItem();
        const int pageSize = WorkItemQuery.MaxPageSize;

        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Is<WorkItemQuery>(q => q.Page == 1), ct)
            .Returns(new WorkItemPage([item1], TotalCount: pageSize + 1, Page: 1, PageSize: pageSize));
        persistence.QueryAsync(Arg.Is<WorkItemQuery>(q => q.Page == 2), ct)
            .Returns(new WorkItemPage([item2], TotalCount: pageSize + 1, Page: 2, PageSize: pageSize));

        persistence.GetByIdAsync(item1.Id, ct).Returns(item1);
        persistence.GetByIdAsync(item2.Id, ct).Returns(item2);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.Received(1).ReplaceAsync(item1, ct);
        await persistence.Received(1).ReplaceAsync(item2, ct);
    }
}
