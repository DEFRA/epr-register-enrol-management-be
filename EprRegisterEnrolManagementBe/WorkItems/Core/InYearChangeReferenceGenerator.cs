using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Produces the <c>applicationReference</c> for in-year change work items
/// (RA-492). Regulators agreed this format on 10 Nov 2026 so they can tell an
/// in-year change apart from an application (<c>AP…</c>, see
/// <see cref="ApplicationReferenceGenerator"/>):
/// <c>IYC-{changeTypeCode}-{yyyy}-{sequence}</c>, for example
/// <c>IYC-ORS-2026-00021</c>.
/// <list type="bullet">
///   <item><c>IYC</c>: In-Year Change, the literal <see cref="Prefix"/>.</item>
///   <item><c>{changeTypeCode}</c>: the change type, taken from the
///   work-item type id through <see cref="TryGetChangeTypeCode"/>.</item>
///   <item><c>{yyyy}</c>: the 4-digit <c>payload.accreditationYear</c>. If it
///   is absent or unusable, the current UTC year from the injected
///   <see cref="TimeProvider"/> is used instead.</item>
///   <item><c>{sequence}</c>: allocated from <see cref="IReferenceSequenceStore"/>,
///   zero-padded to at least 5 digits. There is one counter per change type
///   per year, so <c>IYC-ORS-2027</c> and <c>IYC-SIP-2026</c> each start at
///   <c>00001</c>.</item>
/// </list>
/// Past <c>99999</c> the sequence widens (<c>IYC-ORS-2026-100000</c>) instead
/// of failing. Five digits is a minimum width, so a reference stays unique and
/// keeps its <c>IYC-ORS-yyyy-</c> prefix. Failing would block every later
/// change request of that type until the next year.
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
    /// (from <see cref="InYearChangeReferenceGenerator.TryGetChangeTypeCode"/>).
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

    // The ONLY place a work-item type id is mapped to an in-year change type code. Submitting a
    // type listed here gets an IYC reference; every other type keeps the AP format. This is a
    // plain string table, so Core needs no dependency on the module that will register the
    // type. When the case-management story registers add-overseas-site, its references are
    // IYC-ORS with no further reference work. Future change types (SIP, TON, ...) are one more
    // line here. The comparer matches WorkItemRegistry's type-id lookup.
    private static readonly Dictionary<string, string> s_changeTypeCodes = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["add-overseas-site"] = "ORS",
    };

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

    /// <summary>
    /// True when <paramref name="typeId"/> is an in-year change type, with
    /// its change type code (for example <c>add-overseas-site</c> gives
    /// <c>ORS</c>).
    /// </summary>
    public static bool TryGetChangeTypeCode(
        string typeId,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? changeTypeCode
    ) => s_changeTypeCodes.TryGetValue(typeId, out changeTypeCode);

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
            if (TryGetFourDigitYear(value, out var year))
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

    // Same decimal-based check as ApplicationReferenceGenerator.TryGetOrgNumber. BsonValue.ToInt32()
    // silently wraps an out-of-range Int64 or Double, and it throws for Decimal128.
    private static bool TryGetFourDigitYear(BsonValue value, out int year)
    {
        year = 0;
        if (!value.IsNumeric)
        {
            return false;
        }

        decimal asDecimal;
        try
        {
            asDecimal = value.ToDecimal();
        }
        catch (Exception ex)
            when (ex is OverflowException or FormatException or InvalidCastException)
        {
            return false;
        }

        if (asDecimal < 1000 || asDecimal > 9999 || asDecimal != Math.Truncate(asDecimal))
        {
            return false;
        }

        year = (int)asDecimal;
        return true;
    }
}
