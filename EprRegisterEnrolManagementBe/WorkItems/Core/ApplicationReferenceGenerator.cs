using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Produces the human-facing work-item <c>applicationReference</c>
/// (RA-318). The backend owns reference generation so a client can never
/// supply, spoof or collide a reference.
///
/// Format (RA-318, org segment revised RA-503): <c>AP</c> + 2-digit
/// accreditation year + 2-char agency code + the organisation segment +
/// the last 3 characters of the regulator postcode + the first 2
/// characters of the material, all upper-cased. The organisation segment
/// is the numeric, operator/regulator-safe <c>operatorOrgNumber</c>
/// (e.g. <c>500500</c>, zero-padded to 6 digits) when the payload
/// carries it, falling back to the last 5 characters of
/// <c>operatorOrganisationId</c> (ReEx's internal ObjectId — never
/// otherwise surfaced to an operator or regulator) only when
/// <c>operatorOrgNumber</c> is absent; see
/// <see cref="ResolveOrganisationSegment"/>. The agency code and
/// postcode suffix are both derived from the same postcode, chosen per
/// RA-314 AC01/AC02: an Exporter's is their registered office location
/// (<c>companyRegisterAddressPostcode</c>); a Reprocessor's — and any
/// payload without a <c>wasteProcessingType</c> — is the site location.
/// An Exporter payload missing <c>companyRegisterAddressPostcode</c> fails
/// open to the default England agency code (<c>EA</c>) rather than
/// erroring, matching the existing fallback for a missing
/// <c>wasteProcessingType</c>; a warning is logged so the gap stays
/// observable even though generation still succeeds. The upstream
/// backend (RA-314) is expected to reject such payloads before they
/// reach this generator.
/// RA-503: the result is no longer truncated to a fixed length — this
/// value is no longer used as a BACS payment reference, so the previous
/// 18-character cap (and the disambiguator's character-replacement
/// branch that existed to stay within it) has been removed. Deterministic
/// for a given payload and <paramref name="attempt"/> of 1 — unlike the
/// previous random-suffix format, the same submission always yields the
/// same reference on the first attempt. Payloads with no operator
/// organisation id (e.g. work items created manually via the case
/// management UI, which has no such field) can collide when the site
/// postcode and material also match; <see cref="WorkItemService"/>'s
/// collision-retry loop calls this again with an incremented
/// <paramref name="attempt"/>, and attempts beyond the first append a
/// disambiguator character so retries actually differ instead of
/// repeating the same value forever.
/// </summary>
public interface IApplicationReferenceGenerator
{
    /// <summary>
    /// Generate a reference derived from the submission <paramref name="payload"/>.
    /// <paramref name="attempt"/> is the 1-based retry count from the caller's
    /// collision-retry loop; pass 1 for the initial attempt.
    /// </summary>
    string Generate(BsonDocument payload, int attempt);
}

/// <inheritdoc />
public sealed class ApplicationReferenceGenerator : IApplicationReferenceGenerator
{
    /// <summary>Literal prefix every reference carries.</summary>
    public const string Prefix = "AP";

    private const string DefaultAgencyCode = "EA";

    // Mirrors NationResolver's postcode-area table (WorkItems/ReAccreditation) — duplicated
    // rather than referenced so this Core-layer generator has no dependency on the
    // ReAccreditation module; Core must stay usable by any future work item type.
    private static readonly HashSet<string> s_scotlandPrefixes = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "AB",
        "DD",
        "DG",
        "EH",
        "FK",
        "G",
        "HS",
        "IV",
        "KA",
        "KW",
        "KY",
        "ML",
        "PA",
        "PH",
        "TD",
        "ZE",
    };

    private static readonly HashSet<string> s_walesPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "CF",
        "CH",
        "LD",
        "LL",
        "NP",
        "SA",
        "SY",
    };

    private const string NiPrefix = "BT";

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ApplicationReferenceGenerator> _logger;

    public ApplicationReferenceGenerator(
        TimeProvider? timeProvider = null,
        ILogger<ApplicationReferenceGenerator>? logger = null
    )
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ApplicationReferenceGenerator>.Instance;
    }

    public string Generate(BsonDocument payload, int attempt = 1)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var year = ResolveYear(payload);
        var postcode = ResolveRegulatorPostcode(payload);
        var agency = ResolveAgencyCode(postcode);
        var orgSegment = ResolveOrganisationSegment(payload);
        var postcodeSuffix = PostcodeSuffix(postcode);
        var materialPrefix = MaterialPrefix(GetString(payload, "material"));

        var reference =
            $"{Prefix}{year:D2}{agency}{orgSegment}{postcodeSuffix}{materialPrefix}".ToUpperInvariant();

        // Collision on a prior attempt: append a disambiguator unique to this attempt, keeping
        // the reference recognisably derived from the same payload, so the retry loop actually
        // converges instead of regenerating the same value forever. RA-503: no longer truncated
        // first — this value is no longer used as a BACS payment reference.
        return attempt <= 1 ? reference : reference + DisambiguatorChar(attempt);
    }

    private static char DisambiguatorChar(int attempt) => (char)('0' + (attempt % 10));

    // RA-503: prefer the numeric, operator/regulator-safe organisation number
    // (operatorOrgNumber, e.g. 500500) once the upstream backend sends it — this is the same
    // value RegulatoryNumberGenerator already embeds into accreditation/registration numbers.
    // Fall back to the last 5 characters of operatorOrganisationId (ReEx's internal ObjectId)
    // only when operatorOrgNumber is absent or not a usable 6-digit organisation number — e.g.
    // during the deploy gap before the upstream backend change reaches this environment, for
    // payloads with no numeric org number at all (manually-created case-management work items),
    // or a malformed/out-of-range value — so this generator never regresses below its pre-fix
    // behaviour, and never embeds a garbage or malformed segment into the reference.
    private static string ResolveOrganisationSegment(BsonDocument payload)
    {
        if (
            payload.TryGetValue("operatorOrgNumber", out var orgNumberValue)
            && TryGetOrgNumber(orgNumberValue, out var orgNumber)
        )
        {
            return orgNumber.ToString("D6", CultureInfo.InvariantCulture);
        }

        var organisationId = GetString(payload, "operatorOrganisationId") ?? string.Empty;
        return organisationId.Length > 5 ? organisationId[^5..] : organisationId;
    }

    // RA-503: IsNumeric admits BsonInt64/BsonDouble/BsonDecimal128, none of which BsonValue.ToInt32()
    // can be trusted to convert safely from - it silently wraps for an out-of-range Int64/Double
    // (e.g. long.MaxValue narrows to -1) and throws OverflowException for Decimal128. Validate the
    // value explicitly instead of trusting the BSON type category alone: reject negative (which would
    // inject a literal '-' into what the format assumes is a clean alphanumeric reference), reject
    // non-integral, and cap at 999999 so D6's zero-padding (a MINIMUM width, not a maximum) can never
    // widen the segment past the intended 6 digits.
    private static bool TryGetOrgNumber(BsonValue value, out int orgNumber)
    {
        orgNumber = 0;
        if (!value.IsNumeric)
            return false;

        double asDouble;
        try
        {
            asDouble = value.ToDouble();
        }
        catch (Exception ex)
            when (ex is OverflowException or FormatException or InvalidCastException)
        {
            return false;
        }

        if (
            double.IsNaN(asDouble)
            || double.IsInfinity(asDouble)
            || asDouble < 0
            || asDouble > 999999
            || asDouble != Math.Truncate(asDouble)
        )
            return false;

        orgNumber = (int)asDouble;
        return true;
    }

    private int ResolveYear(BsonDocument payload)
    {
        if (payload.TryGetValue("accreditationYear", out var value) && value.IsNumeric)
        {
            return value.ToInt32() % 100;
        }

        return _timeProvider.GetUtcNow().UtcDateTime.Year % 100;
    }

    private static string ResolveAgencyCode(string? postcode)
    {
        var area = ExtractAreaCode(postcode);
        if (area is null)
        {
            return DefaultAgencyCode;
        }

        if (area.Equals(NiPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return "NI";
        }

        if (s_scotlandPrefixes.Contains(area))
        {
            return "SE";
        }

        if (s_walesPrefixes.Contains(area))
        {
            return "NR";
        }

        return DefaultAgencyCode;
    }

    private static string? ExtractAreaCode(string? postcode)
    {
        if (string.IsNullOrWhiteSpace(postcode))
        {
            return null;
        }

        var trimmed = postcode.TrimStart();
        var length = 0;
        while (length < trimmed.Length && char.IsLetter(trimmed[length]))
        {
            length++;
        }

        return length == 0 ? null : trimmed[..length];
    }

    private static string PostcodeSuffix(string? postcode)
    {
        if (string.IsNullOrWhiteSpace(postcode))
        {
            return string.Empty;
        }

        var compact = postcode.Replace(" ", string.Empty);
        return compact.Length <= 3 ? compact : compact[^3..];
    }

    private static string MaterialPrefix(string? material)
    {
        if (string.IsNullOrWhiteSpace(material))
        {
            return string.Empty;
        }

        return material.Length <= 2 ? material : material[..2];
    }

    private static string? GetString(BsonDocument payload, string key) =>
        payload.TryGetValue(key, out var value) && value.IsString ? value.AsString : null;

    // Two real callers disagree on shape: the operator-facing backend BFF
    // sends siteAddress as a plain string plus a separate flat
    // siteAddressPostcode key (HttpCaseWorkingApiAdapter.BuildPayload), while
    // the case-management admin UI nests it as siteAddress.postcode (matching
    // its Joi schema and ReAccreditationNationRoutingHook.ExtractPostcode).
    // Support both rather than picking one and silently losing the other.
    private static string? ExtractPostcode(BsonDocument payload)
    {
        var flat = GetString(payload, "siteAddressPostcode");
        if (flat is not null)
        {
            return flat;
        }

        if (!payload.TryGetValue("siteAddress", out var siteAddress) || !siteAddress.IsBsonDocument)
        {
            return null;
        }

        var doc = siteAddress.AsBsonDocument;
        return doc.TryGetValue("postcode", out var value) && value.IsString ? value.AsString : null;
    }

    // RA-314 AC01/AC02: an Exporter's payment reference is derived from their
    // registered office location; a Reprocessor's is derived from the site
    // location. wasteProcessingType ("exporter" | "reprocessor") is set by
    // HttpCaseWorkingApiAdapter.BuildPayload in the operator-facing backend;
    // payloads without it (e.g. the case-management admin UI) fall back to
    // the site postcode, matching pre-RA-314 behaviour.
    private string? ResolveRegulatorPostcode(BsonDocument payload)
    {
        var wasteProcessingType = GetString(payload, "wasteProcessingType");
        var isExporter =
            wasteProcessingType?.Equals("exporter", StringComparison.OrdinalIgnoreCase) == true;

        if (!isExporter)
        {
            return ExtractPostcode(payload);
        }

        var registeredOfficePostcode = GetString(payload, "companyRegisterAddressPostcode");
        if (string.IsNullOrWhiteSpace(registeredOfficePostcode))
        {
            // Fail-open by design (see class summary) rather than blocking
            // work-item submission on a data gap the upstream backend
            // should already prevent — but the gap must stay visible.
            _logger.LogWarning(
                "Exporter payload for operatorOrganisationId {OperatorOrganisationId} has no "
                    + "companyRegisterAddressPostcode; falling open to the default England ({DefaultAgencyCode}) "
                    + "agency code for the payment reference.",
                GetString(payload, "operatorOrganisationId"),
                DefaultAgencyCode
            );
        }

        return registeredOfficePostcode;
    }
}

/// <summary>
/// DI helper so the generator is registered in exactly one place, mirroring
/// the framework's other Core service registrations.
/// </summary>
[ExcludeFromCodeCoverage]
public static class ApplicationReferenceGeneratorExtensions
{
    public static IServiceCollection AddApplicationReferenceGenerator(
        this IServiceCollection services
    )
    {
        services.AddSingleton<IApplicationReferenceGenerator, ApplicationReferenceGenerator>();
        return services;
    }
}
