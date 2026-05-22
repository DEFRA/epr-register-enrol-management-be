using System.Globalization;
using NUlid;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-133 default <see cref="IAccreditationIdGenerator"/>. Produces ids of
/// the shape <c>ACC-{Year}-{Material[:1]}-{ULID8}</c> where:
/// <list type="bullet">
///   <item><c>{Year}</c> is the four-digit accreditation year supplied
///   by the caller.</item>
///   <item><c>{Material[:1]}</c> is the uppercase first character of the
///   supplied material; falls back to <c>X</c> when the material is null
///   or whitespace.</item>
///   <item><c>{ULID8}</c> is the last eight characters of a freshly
///   generated ULID, uppercased — Crockford base32 already excludes
///   confusable characters, so the suffix is human-friendly.</item>
/// </list>
/// Uniqueness is enforced by a pre-flight lookup against the persisted
/// work item collection. On collision the generator regenerates; if
/// <see cref="MaxAttempts"/> consecutive collisions occur an
/// <see cref="InvalidOperationException"/> is thrown so the calling
/// approval service can surface the failure to the operator.
/// </summary>
internal sealed class AccreditationIdGenerator(IAccreditationIdLookup lookup) : IAccreditationIdGenerator
{
    internal const int MaxAttempts = 5;
    private const char MaterialFallback = 'X';

    public async Task<string> GenerateAsync(
        string? material,
        int year,
        CancellationToken cancellationToken = default)
    {
        var materialChar = ResolveMaterialChar(material);
        var yearSegment = year.ToString("D4", CultureInfo.InvariantCulture);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var ulid = Ulid.NewUlid().ToString();
            var suffix = ulid[^8..].ToUpperInvariant();
            var candidate = $"ACC-{yearSegment}-{materialChar}-{suffix}";

            if (!await lookup.ExistsAsync(candidate, cancellationToken))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Unable to generate a unique accreditation id after {MaxAttempts} attempts.");
    }

    private static char ResolveMaterialChar(string? material)
    {
        if (string.IsNullOrWhiteSpace(material))
        {
            return MaterialFallback;
        }
        return char.ToUpperInvariant(material[0]);
    }
}
