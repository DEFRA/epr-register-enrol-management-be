using EprRegisterEnrolManagementBe.Utils.Mongo;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-769: the "start date of accreditation X" rule, without the web host or MongoDB. The payload is built through
/// <see cref="ReAccreditationPayload"/>, as the approval service stamps it.
/// </summary>
public class ReAccreditationStartDateResolverTests
{
    private const string Number = "A26ER5003240164GR";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    static ReAccreditationStartDateResolverTests() => MongoConventions.Register();

    private static (ReAccreditationStartDateResolver Resolver, IWorkItemPersistence Persistence) Build(
        WorkItem? issuer
    )
    {
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence
            .FindBySubmissionKeyAsync(
                ReAccreditationType.Id,
                ReAccreditationAccreditationNumber.PayloadKey,
                Number,
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(issuer));
        return (new ReAccreditationStartDateResolver(persistence), persistence);
    }

    private static WorkItem Issuer(string stateId, BsonDocument payload) =>
        new()
        {
            TypeId = ReAccreditationType.Id,
            StateId = stateId,
            Payload = payload,
        };

    private static BsonDocument Payload(DateOnly? startDate) =>
        new ReAccreditationPayload { AccreditationId = Number, AccreditationStartDate = startDate }.ToBsonDocument();

    [Fact]
    public async Task A_granted_issuer_supplies_its_stored_start_date()
    {
        var issuer = Issuer(ReAccreditationType.ApprovedStateId, Payload(new DateOnly(2026, 6, 15)));
        var (resolver, _) = Build(issuer);

        var lookup = await resolver.FindAsync(Number, Ct);

        Assert.Equal(new DateOnly(2026, 6, 15), lookup.Date);
        Assert.Equal(StartDateMiss.None, lookup.Miss);
        Assert.Equal(issuer.Id, lookup.IssuerWorkItemId);
    }

    [Fact]
    public async Task An_issuer_not_yet_granted_does_not_supply_a_date_even_if_it_carries_one()
    {
        var issuer = Issuer("approved-pending-contact", Payload(new DateOnly(2026, 6, 15)));
        var (resolver, _) = Build(issuer);

        var lookup = await resolver.FindAsync(Number, Ct);

        Assert.Null(lookup.Date);
        Assert.Equal(StartDateMiss.IssuerNotGranted, lookup.Miss);
        Assert.Equal(issuer.Id, lookup.IssuerWorkItemId);
    }

    [Fact]
    public async Task A_granted_issuer_without_a_date_is_a_miss()
    {
        var (resolver, _) = Build(Issuer(ReAccreditationType.ApprovedStateId, Payload(null)));

        var lookup = await resolver.FindAsync(Number, Ct);

        Assert.Null(lookup.Date);
        Assert.Equal(StartDateMiss.NoUsableStartDate, lookup.Miss);
    }

    [Theory]
    [InlineData("not a date")]
    [InlineData("15/06/2026")]
    public async Task A_granted_issuer_with_an_unreadable_date_is_a_miss(string stored)
    {
        var payload = new BsonDocument { ["accreditationId"] = Number, ["accreditationStartDate"] = stored };
        var (resolver, _) = Build(Issuer(ReAccreditationType.ApprovedStateId, payload));

        var lookup = await resolver.FindAsync(Number, Ct);

        Assert.Null(lookup.Date);
        Assert.Equal(StartDateMiss.NoUsableStartDate, lookup.Miss);
    }

    [Fact]
    public async Task No_issuing_work_item_is_a_miss()
    {
        var (resolver, _) = Build(issuer: null);

        var lookup = await resolver.FindAsync(Number, Ct);

        Assert.Null(lookup.Date);
        Assert.Equal(StartDateMiss.NoIssuingWorkItem, lookup.Miss);
        Assert.Null(lookup.IssuerWorkItemId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_blank_accreditation_number_is_a_miss_without_a_lookup(string? number)
    {
        var (resolver, persistence) = Build(issuer: null);

        var lookup = await resolver.FindAsync(number, Ct);

        Assert.Equal(StartDateMiss.NoAccreditationNumber, lookup.Miss);
        await persistence
            .DidNotReceiveWithAnyArgs()
            .FindBySubmissionKeyAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task A_persistence_failure_propagates()
    {
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence
            .FindBySubmissionKeyAsync(default!, default!, default!, Ct)
            .ReturnsForAnyArgs(Task.FromException<WorkItem?>(new InvalidOperationException("db down")));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ReAccreditationStartDateResolver(persistence).FindAsync(Number, Ct)
        );
    }
}
