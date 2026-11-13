using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Produces the <c>applicationReference</c> for in-year change work items
/// (RA-492). The format is <c>IYC-{changeTypeCode}-{yyyy}-{sequence}</c>, for
/// example <c>IYC-ORS-2026-00021</c>, so regulators can tell an in-year change
/// apart from an application (<c>AP…</c>, see
/// <see cref="ApplicationReferenceGenerator"/>).
/// <list type="bullet">
///   <item><c>IYC</c>: In-Year Change, the literal <see cref="Prefix"/>.</item>
///   <item><c>{changeTypeCode}</c>: the type's own
///   <see cref="IWorkItemType.InYearChangeCode"/>.</item>
///   <item><c>{yyyy}</c>: the payload's 4-digit <c>accreditationYear</c>, which
///   is mandatory. <see cref="IInYearChangeReferenceGenerator.Validate"/> checks
///   it, and <see cref="WorkItemService"/> rejects a submission without a usable
///   year before any number is allocated.</item>
///   <item><c>{sequence}</c>: 5 digits, from a counter in
///   <see cref="IReferenceSequenceStore"/> keyed <c>IYC-{code}-{yyyy}</c>, so
///   there is one sequence per change type per year. The first reference for a
///   key is <c>00001</c>. The stored counter only grows, and the reference
///   cycles through <c>00001</c> to <see cref="MaxSequence"/>, so <c>99999</c>
///   is followed by <c>00001</c>. <c>00000</c> is never shown.</item>
/// </list>
/// The format, the mandatory year and the wrap are RA-492 decisions. RA-739
/// moved the start (and the wrap target) from <c>00000</c> to <c>00001</c>.
/// <para>
/// The wrap acts as a cap. A counter's key fixes both the change type and the
/// year, so after a wrap every earlier number already exists with the same
/// type and year. The <c>applicationReference</c> unique index makes each
/// retry take the next number, and every one collides, so once a type passes
/// 99,999 changes in a year each later submission of it fails with 503
/// (<see cref="WorkItemActionFailureCode.ApplicationReferenceExhausted"/>)
/// until the year changes. RA-492 accepts this as unrealistic.
/// </para>
/// <para>
/// Unlike the deterministic AP format, every call allocates a fresh number.
/// <see cref="WorkItemService"/> therefore calls this, through
/// <see cref="IWorkItemReferenceGenerator"/>, only after its idempotent-replay
/// check and payload validation. It calls it again on an
/// <c>applicationReference</c> unique-index collision. The retry takes the next
/// number and never gets a disambiguator character appended.
/// </para>
/// </summary>
public interface IInYearChangeReferenceGenerator
{
    /// <summary>
    /// Why <paramref name="payload"/> cannot be given an in-year change
    /// reference, or <c>null</c> when it can. The only requirement is a 4-digit
    /// <c>accreditationYear</c>.
    /// </summary>
    string? Validate(BsonDocument payload);

    /// <summary>
    /// Allocate the next reference for <paramref name="changeTypeCode"/> (a
    /// type's <see cref="IWorkItemType.InYearChangeCode"/>). The caller must
    /// first check the payload with <see cref="Validate"/>.
    /// </summary>
    Task<string> GenerateAsync(
        string changeTypeCode,
        BsonDocument payload,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// As <see cref="GenerateAsync(string, BsonDocument, CancellationToken)"/>,
    /// but allocates in the caller's <paramref name="session"/> (RA-739), so the
    /// number is used only if the caller's transaction commits.
    /// </summary>
    Task<string> GenerateAsync(
        IClientSessionHandle session,
        string changeTypeCode,
        BsonDocument payload,
        CancellationToken cancellationToken = default
    );
}

/// <inheritdoc />
public sealed class InYearChangeReferenceGenerator(IReferenceSequenceStore sequenceStore)
    : IInYearChangeReferenceGenerator
{
    /// <summary>Literal prefix every in-year change reference carries.</summary>
    public const string Prefix = "IYC";

    /// <summary>
    /// The highest number the 5-digit sequence segment shows. The store hands
    /// out 1, 2, 3, ...; the segment shows <c>00001</c> to this value and then
    /// wraps back to <c>00001</c>.
    /// </summary>
    public const long MaxSequence = 99_999;

    public string? Validate(BsonDocument payload) =>
        AccreditationYear.TryRead(payload, out _)
            ? null
            : $"payload.{AccreditationYear.Field} must be a 4-digit year for an in-year change.";

    public Task<string> GenerateAsync(
        string changeTypeCode,
        BsonDocument payload,
        CancellationToken cancellationToken = default
    ) => AllocateReferenceAsync(session: null, changeTypeCode, payload, cancellationToken);

    public Task<string> GenerateAsync(
        IClientSessionHandle session,
        string changeTypeCode,
        BsonDocument payload,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(session);
        return AllocateReferenceAsync(session, changeTypeCode, payload, cancellationToken);
    }

    // As MongoReferenceSequenceStore.AllocateAsync: a null session allocates
    // outside any transaction.
    private async Task<string> AllocateReferenceAsync(
        IClientSessionHandle? session,
        string changeTypeCode,
        BsonDocument payload,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(changeTypeCode);
        // Unreachable from WorkItemService, which rejects the submission first
        // (Validate). Checked here so no number is ever allocated for a bad year.
        if (!AccreditationYear.TryRead(payload, out var year))
        {
            throw new ArgumentException(Validate(payload), nameof(payload));
        }

        var sequenceKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}-{changeTypeCode}-{year:D4}"
        );
        var sequence = session is null
            ? await sequenceStore.NextAsync(sequenceKey, cancellationToken)
            : await sequenceStore.NextAsync(session, sequenceKey, cancellationToken);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{sequenceKey}-{(sequence - 1) % MaxSequence + 1:D5}"
        );
    }
}
