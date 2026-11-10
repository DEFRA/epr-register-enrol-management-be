using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Produces the <c>applicationReference</c> for in-year change work items
/// (RA-492). The Jira comment of 10 Nov 2026 (regulators agreed) fixes
/// <c>IYC-{changeTypeCode}-{year}-{5-digit sequence}</c>, with one sequence per
/// change type per year, for example <c>IYC-ORS-2026-00021</c>. That lets
/// regulators tell an in-year change apart from an application (<c>AP…</c>,
/// see <see cref="ApplicationReferenceGenerator"/>).
/// <list type="bullet">
///   <item><c>IYC</c>: In-Year Change, the literal <see cref="Prefix"/>.</item>
///   <item><c>{changeTypeCode}</c>: the type's own
///   <see cref="IWorkItemType.InYearChangeCode"/>.</item>
///   <item><c>{year}</c>: see the implementation choices below.</item>
///   <item><c>{sequence}</c>: allocated from <see cref="IReferenceSequenceStore"/>,
///   keyed <c>IYC-{code}-{yyyy}</c>.</item>
/// </list>
/// Implementation choices, not fixed by the Jira comment and still to be
/// confirmed with the PO:
/// <list type="bullet">
///   <item>The year is the 4-digit <c>payload.accreditationYear</c>. If it is
///   absent or unusable, the current UTC year from the injected
///   <see cref="TimeProvider"/> is used instead.</item>
///   <item>Each counter starts at <c>00001</c>.</item>
///   <item>Past <c>99999</c> the sequence widens (<c>IYC-ORS-2026-100000</c>)
///   instead of failing. Five digits is a minimum width, so the reference
///   stays unique and keeps its <c>IYC-ORS-yyyy-</c> prefix. Failing would
///   block every later change request of that type until the next year.</item>
/// </list>
/// <para>
/// Unlike the deterministic AP format, every call allocates a fresh number.
/// <see cref="WorkItemService"/> therefore calls this, through
/// <see cref="IWorkItemReferenceGenerator"/>, only after its
/// idempotent-replay check, and calls it again on an
/// <c>applicationReference</c> unique-index collision. The retry takes the
/// next number and never gets a disambiguator character appended.
/// </para>
/// </summary>
public interface IInYearChangeReferenceGenerator
{
    /// <summary>
    /// Allocate the next reference for <paramref name="changeTypeCode"/>
    /// (a type's <see cref="IWorkItemType.InYearChangeCode"/>).
    /// </summary>
    Task<string> GenerateAsync(
        string changeTypeCode,
        BsonDocument payload,
        CancellationToken cancellationToken = default
    );
}

/// <inheritdoc />
public sealed class InYearChangeReferenceGenerator : IInYearChangeReferenceGenerator
{
    /// <summary>Literal prefix every in-year change reference carries.</summary>
    public const string Prefix = "IYC";

    /// <summary>Minimum width the sequence segment is zero-padded to.</summary>
    public const int SequenceMinimumDigits = 5;

    private const string AccreditationYearField = "accreditationYear";

    private readonly IReferenceSequenceStore _sequenceStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InYearChangeReferenceGenerator> _logger;

    public InYearChangeReferenceGenerator(
        IReferenceSequenceStore sequenceStore,
        TimeProvider? timeProvider = null,
        ILogger<InYearChangeReferenceGenerator>? logger = null
    )
    {
        _sequenceStore = sequenceStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<InYearChangeReferenceGenerator>.Instance;
    }

    public async Task<string> GenerateAsync(
        string changeTypeCode,
        BsonDocument payload,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(changeTypeCode);
        ArgumentNullException.ThrowIfNull(payload);

        var year = ResolveYear(payload);
        var sequenceKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}-{changeTypeCode}-{year:D4}"
        );
        var sequence = await _sequenceStore.NextAsync(sequenceKey, cancellationToken);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{sequenceKey}-{sequence.ToString("D" + SequenceMinimumDigits, CultureInfo.InvariantCulture)}"
        );
    }

    // The operator backend sends accreditationYear as a 4-digit int. Anything that is not a whole
    // number from 1000 to 9999 falls back to the UTC year, so a malformed value can never put a
    // wrong-width or signed year segment into the reference. A value that is present but
    // unusable is logged, because the fallback hides a data gap.
    private int ResolveYear(BsonDocument payload)
    {
        if (payload.TryGetValue(AccreditationYearField, out var value))
        {
            if (BsonWholeNumber.TryGet(value, 1000, 9999, out var year))
            {
                return year;
            }

            _logger.LogWarning(
                "In-year change payload has an unusable {Field} ({BsonType}); using the current UTC year",
                AccreditationYearField,
                value.BsonType
            );
        }

        return _timeProvider.GetUtcNow().UtcDateTime.Year;
    }
}
