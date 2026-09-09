using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

public class ReAccreditationNationRepresentationMigrationTests
{
    private static readonly DateTimeOffset s_now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private static WorkItem BuildIntNationItem(int ordinal) =>
        new()
        {
            TypeId = ReAccreditationType.Id,
            StateId = "duly-made",
            Payload = new BsonDocument
            {
                ["applicationReference"] = "RA-100000292",
                // The corrupted representation: the driver's default ordinal int,
                // exactly what a pre-RA-551 ToBsonDocument() merge cycle wrote.
                ["nation"] = ordinal,
            },
        };

    private static WorkItem BuildStringNationItem(Nation nation) =>
        new()
        {
            TypeId = ReAccreditationType.Id,
            StateId = "duly-made",
            Payload = new BsonDocument
            {
                ["applicationReference"] = "RA-100000293",
                ["nation"] = nation.ToString(),
            },
        };

    private static ReAccreditationNationRepresentationMigration BuildSut() =>
        new(NullLogger<ReAccreditationNationRepresentationMigration>.Instance, new FakeTimeProvider(s_now));

    private static IWorkItemPersistence PersistenceWith(params WorkItem[] items)
    {
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence
            .QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage(items, items.Length, 1, WorkItemQuery.MaxPageSize));
        foreach (var item in items)
        {
            persistence.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        }
        return persistence;
    }

    [Theory]
    [InlineData(0, "England")]
    [InlineData(1, "Scotland")]
    [InlineData(2, "Wales")]
    [InlineData(3, "NorthernIreland")]
    public async Task ApplyAsync_corrects_an_int_nation_to_the_matching_enum_name_string(
        int ordinal,
        string expectedName)
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildIntNationItem(ordinal);
        var persistence = PersistenceWith(item);
        var sut = BuildSut();

        await sut.ApplyAsync(persistence, ct);

        Assert.Equal(BsonType.String, item.Payload["nation"].BsonType);
        Assert.Equal(expectedName, item.Payload["nation"].AsString);
        await persistence.Received(1).ReplaceAsync(item, ct);

        var entry = item.AuditLog.Single(e => e.Action == "nation-representation-corrected");
        Assert.Equal("Nation representation corrected", entry.ActionDisplayName);
        Assert.Equal("migration", entry.CreatedBy);
        Assert.Equal(ordinal.ToString(), entry.Details!["from"]);
        Assert.Equal(expectedName, entry.Details!["to"]);
        Assert.Equal(s_now.UtcDateTime, entry.CreatedAt);
    }

    [Theory]
    [InlineData(Nation.England)]
    [InlineData(Nation.Scotland)]
    [InlineData(Nation.Wales)]
    [InlineData(Nation.NorthernIreland)]
    public async Task ApplyAsync_leaves_a_string_nation_untouched(Nation nation)
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildStringNationItem(nation);
        var persistence = PersistenceWith(item);
        var sut = BuildSut();

        await sut.ApplyAsync(persistence, ct);

        Assert.Equal(nation.ToString(), item.Payload["nation"].AsString);
        await persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_is_idempotent_a_second_run_is_a_no_op()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildIntNationItem(1);
        var persistence = PersistenceWith(item);
        var sut = BuildSut();

        await sut.ApplyAsync(persistence, ct);
        persistence.ClearReceivedCalls();
        await sut.ApplyAsync(persistence, ct);

        Assert.Equal("Scotland", item.Payload["nation"].AsString);
        await persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
        // Exactly one correction audit entry across both runs, not two.
        Assert.Single(item.AuditLog, e => e.Action == "nation-representation-corrected");
    }

    [Fact]
    public async Task ApplyAsync_skips_items_with_no_nation_field_at_all()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            Payload = new BsonDocument { ["applicationReference"] = "RA-100000294" },
        };
        var persistence = PersistenceWith(item);
        var sut = BuildSut();

        await sut.ApplyAsync(persistence, ct);

        await persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_skips_an_out_of_range_ordinal_and_leaves_it_untouched()
    {
        // Defensive: an ordinal outside the known Nation enum range should never
        // occur in practice, but must not be guessed at - leave it for manual
        // investigation rather than writing a wrong value.
        var ct = TestContext.Current.CancellationToken;
        var item = BuildIntNationItem(99);
        var persistence = PersistenceWith(item);
        var sut = BuildSut();

        await sut.ApplyAsync(persistence, ct);

        Assert.Equal(BsonType.Int32, item.Payload["nation"].BsonType);
        await persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_swallows_concurrency_conflicts_and_continues()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildIntNationItem(2);
        var persistence = PersistenceWith(item);
        persistence
            .ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new WorkItemConcurrencyException(item.Id, expectedVersion: 0)));
        var sut = BuildSut();

        var ex = await Record.ExceptionAsync(() => sut.ApplyAsync(persistence, ct));

        Assert.Null(ex);
    }
}
