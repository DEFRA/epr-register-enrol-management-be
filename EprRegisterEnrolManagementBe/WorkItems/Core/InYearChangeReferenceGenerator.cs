using System.Globalization;
using MongoDB.Bson;

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
///   is mandatory. <see cref="TryGetAccreditationYear"/> checks it, and
///   <see cref="WorkItemService"/> rejects a submission without a usable year
///   before any number is allocated.</item>
///   <item><c>{sequence}</c>: 5 digits, from a counter in
///   <see cref="IReferenceSequenceStore"/> keyed <c>IYC-{code}-{yyyy}</c>, so
///   there is one sequence per change type per year. The first reference for a
///   key is <c>00000</c>. The stored counter only grows, and the reference shows
///   it modulo <see cref="SequenceModulus"/>, so <c>99999</c> is followed by
///   <c>00000</c>.</item>
/// </list>
/// The shape and the per-type, per-year sequence were agreed with regulators
/// (Jira comment, 10 Nov 2026). The mandatory year, the <c>00000</c> start and
/// the wrap after <c>99999</c> are Tom's decisions of the same date.
/// <para>
/// Wrap hazard (accepted): after a wrap, an earlier reference with the same
/// number already exists. The <c>applicationReference</c> unique index makes
/// the retry loop take the next number, so in a year with more than 100,000
/// changes of one type, submissions start failing once the retry budget is
/// used up.
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
    /// Allocate the next reference for <paramref name="changeTypeCode"/> (a
    /// type's <see cref="IWorkItemType.InYearChangeCode"/>). The caller must
    /// first check the payload with
    /// <see cref="InYearChangeReferenceGenerator.TryGetAccreditationYear"/>.
    /// </summary>
    Task<string> GenerateAsync(
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

    /// <summary>Payload field holding the mandatory 4-digit accreditation year.</summary>
    public const string AccreditationYearField = "accreditationYear";

    /// <summary>
    /// The sequence segment shows the counter modulo this value, zero-padded to
    /// 5 digits, so it wraps from <c>99999</c> to <c>00000</c>.
    /// </summary>
    public const long SequenceModulus = 100_000;

    /// <summary>
    /// True when <paramref name="payload"/> carries a usable
    /// <c>accreditationYear</c>: a numeric whole number from 1000 to 9999.
    /// </summary>
    public static bool TryGetAccreditationYear(BsonDocument payload, out int year)
    {
        ArgumentNullException.ThrowIfNull(payload);
        year = 0;
        return payload.TryGetValue(AccreditationYearField, out var value)
            && BsonWholeNumber.TryGet(value, 1000, 9999, out year);
    }

    public async Task<string> GenerateAsync(
        string changeTypeCode,
        BsonDocument payload,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(changeTypeCode);
        if (!TryGetAccreditationYear(payload, out var year))
        {
            // Unreachable from WorkItemService, which rejects the submission
            // first. Checked here so no number is ever allocated for a bad year.
            throw new ArgumentException(
                $"payload.{AccreditationYearField} must be a 4-digit year.",
                nameof(payload)
            );
        }

        var sequenceKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}-{changeTypeCode}-{year:D4}"
        );
        var sequence = await sequenceStore.NextAsync(sequenceKey, cancellationToken);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{sequenceKey}-{sequence % SequenceModulus:D5}"
        );
    }
}
