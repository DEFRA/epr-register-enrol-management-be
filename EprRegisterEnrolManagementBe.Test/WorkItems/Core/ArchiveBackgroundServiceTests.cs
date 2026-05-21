using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// Unit tests for <see cref="ArchiveBackgroundService.RunOnceAsync"/>.
/// Persistence is substituted; <see cref="FakeTimeProvider"/> controls "now".
/// </summary>
public class ArchiveBackgroundServiceTests
{
    private static readonly DateTimeOffset s_fixedNow =
        new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static WorkItem ApprovedItem(int daysAgo, bool alreadyArchived = false)
    {
        var lastModified = s_fixedNow.AddDays(-daysAgo).UtcDateTime;
        var payload = new BsonDocument();
        if (alreadyArchived)
        {
            payload[ArchiveBackgroundService.ArchivedAtPayloadKey] = new BsonDateTime(lastModified);
        }
        return new WorkItem
        {
            TypeId = "re-accreditation",
            StateId = "approved",
            SubmittedBy = "test-client",
            Payload = payload,
            LastModifiedAt = lastModified,
            SubmittedAt = lastModified
        };
    }

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
            config);

        return new Sut(service, persistence);
    }

    [Fact]
    public async Task RunOnceAsync_stamps_archivedAt_on_old_approved_item()
    {
        // Must be strictly MORE than ArchiveAfterDays old to qualify.
        var item = ApprovedItem(daysAgo: ArchiveBackgroundService.ArchiveAfterDays + 1);
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(item.Payload.Contains(ArchiveBackgroundService.ArchivedAtPayloadKey));
        await sut.Persistence.Received(1).ReplaceAsync(item, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnceAsync_skips_item_not_yet_old_enough()
    {
        var item = ApprovedItem(daysAgo: ArchiveBackgroundService.ArchiveAfterDays - 1);
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.False(item.Payload.Contains(ArchiveBackgroundService.ArchivedAtPayloadKey));
        await sut.Persistence.DidNotReceive().ReplaceAsync(item, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnceAsync_skips_already_archived_item()
    {
        var item = ApprovedItem(daysAgo: ArchiveBackgroundService.ArchiveAfterDays + 1, alreadyArchived: true);
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        await sut.Persistence.DidNotReceive().ReplaceAsync(item, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnceAsync_queries_approved_items_with_include_archived()
    {
        var sut = Build();

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        await sut.Persistence.Received(1).QueryAsync(
            Arg.Is<WorkItemQuery>(q =>
                q.StateIds != null &&
                q.StateIds.Contains("approved") &&
                q.IncludeArchived),
            Arg.Any<CancellationToken>());
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
        var item = ApprovedItem(daysAgo: ArchiveBackgroundService.ArchiveAfterDays);
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        await sut.Persistence.DidNotReceive().ReplaceAsync(item, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnceAsync_concurrency_exception_on_replace_is_swallowed()
    {
        var item = ApprovedItem(daysAgo: ArchiveBackgroundService.ArchiveAfterDays + 1);
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
        var item = ApprovedItem(daysAgo: ArchiveBackgroundService.ArchiveAfterDays + 1);
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
        var item = ApprovedItem(daysAgo: ArchiveBackgroundService.ArchiveAfterDays + 1);
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(s_fixedNow.UtcDateTime, item.LastModifiedAt);
    }
}
