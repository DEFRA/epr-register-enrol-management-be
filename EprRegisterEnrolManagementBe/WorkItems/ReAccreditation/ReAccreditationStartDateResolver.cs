using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>Why <see cref="IReAccreditationStartDateResolver"/> found no start date. Used for the caller's log.</summary>
internal enum StartDateMiss
{
    /// <summary>A date was found (no miss).</summary>
    None,
    NoAccreditationNumber,

    /// <summary>No re-accreditation work item holds this accreditation number (a different or corrected number, or
    /// an accreditation not issued through a re-accreditation case).</summary>
    NoIssuingWorkItem,

    /// <summary>The issuing work item is not Granted yet: the number is minted at step 1, the date at step 2.</summary>
    IssuerNotGranted,

    /// <summary>The issuing work item is Granted but carries no usable start date.</summary>
    NoUsableStartDate,
}

/// <summary>The stored start date of an accreditation, or why there is none.</summary>
internal readonly record struct StartDateLookup(DateOnly? Date, StartDateMiss Miss, Guid? IssuerWorkItemId = null)
{
    public static StartDateLookup Found(DateOnly date, Guid issuerId) => new(date, StartDateMiss.None, issuerId);

    public static StartDateLookup Missed(StartDateMiss miss, Guid? issuerId = null) => new(null, miss, issuerId);
}

/// <summary>
/// RA-769: the "start date of accreditation X" rule. The re-accreditation work item that issued an accreditation
/// stamps its start date on <see cref="ReAccreditationPayload.AccreditationStartDate"/> when it is Granted.
/// </summary>
internal interface IReAccreditationStartDateResolver
{
    /// <summary>
    /// The start date stamped on the Granted re-accreditation work item that issued
    /// <paramref name="accreditationNumber"/>. It counts only once that work item is Granted (<c>approved</c>): the
    /// number is minted at step 1 (<c>approved-pending-contact</c>) but the date is stamped at step 2. Never throws
    /// for a missing or unreadable date (a <see cref="StartDateLookup"/> with a <see cref="StartDateMiss"/>); a
    /// persistence failure propagates.
    /// </summary>
    Task<StartDateLookup> FindAsync(string? accreditationNumber, CancellationToken cancellationToken = default);
}

internal sealed class ReAccreditationStartDateResolver(IWorkItemPersistence persistence)
    : IReAccreditationStartDateResolver
{
    public async Task<StartDateLookup> FindAsync(
        string? accreditationNumber,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(accreditationNumber))
        {
            return StartDateLookup.Missed(StartDateMiss.NoAccreditationNumber);
        }

        // Uses the unique partial index on payload.accreditationId.
        var issuer = await persistence.FindBySubmissionKeyAsync(
            ReAccreditationType.Id,
            ReAccreditationAccreditationNumber.PayloadKey,
            accreditationNumber,
            cancellationToken
        );
        if (issuer is null)
        {
            return StartDateLookup.Missed(StartDateMiss.NoIssuingWorkItem);
        }
        if (issuer.StateId != ReAccreditationType.ApprovedStateId)
        {
            return StartDateLookup.Missed(StartDateMiss.IssuerNotGranted, issuer.Id);
        }

        var date = ReadStartDate(issuer.Payload);
        return date is { } found
            ? StartDateLookup.Found(found, issuer.Id)
            : StartDateLookup.Missed(StartDateMiss.NoUsableStartDate, issuer.Id);
    }

    private static DateOnly? ReadStartDate(BsonDocument? payload)
    {
        try
        {
            return BsonSerializer.Deserialize<ReAccreditationPayload>(payload ?? new BsonDocument()).AccreditationStartDate;
        }
        catch (Exception ex) when (ex is BsonSerializationException or FormatException or InvalidCastException)
        {
            // An unreadable payload is a miss (logged by the caller), as an absent date is.
            return null;
        }
    }
}
