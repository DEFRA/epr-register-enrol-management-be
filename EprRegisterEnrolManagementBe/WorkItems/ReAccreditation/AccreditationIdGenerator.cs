using System.Globalization;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-133/epr-accreditation-id-format default <see cref="IAccreditationIdGenerator"/>.
/// Produces fixed-width, 16-character ids of the shape
/// <c>A{Year:2}{Agency:1}{OperatorType:1}{OrgId:6}{PostcodeSuffix:3}{Material:2}</c>
/// where:
/// <list type="bullet">
///   <item><c>{Year:2}</c> is the last two digits of the accreditation year
///   supplied by the caller.</item>
///   <item><c>{Agency:1}</c> is the single-letter UK nation code (E/S/W/N)
///   derived from the regulator postcode via <see cref="INationResolver"/>.</item>
///   <item><c>{OperatorType:1}</c> is <c>R</c> (reprocessor) or <c>X</c>
///   (exporter), read from <c>payload.wasteProcessingType</c>; anything
///   other than <c>"exporter"</c> (including absent) defaults to <c>R</c>,
///   matching <see cref="Core.ApplicationReferenceGenerator"/>'s fallback.</item>
///   <item><c>{OrgId:6}</c> is the last six characters of
///   <c>payload.operatorOrganisationId</c>, left-padded with <c>0</c> when
///   shorter.</item>
///   <item><c>{PostcodeSuffix:3}</c> is the last three characters of the
///   same regulator postcode (space-stripped), left-padded with <c>0</c>
///   when shorter.</item>
///   <item><c>{Material:2}</c> is looked up from a closed table
///   (plastic→PL, steel→ST, wood→WO, aluminium→AL, glass→GL), falling
///   back to <c>XX</c> for anything else — Glass Remelt/Other are out of
///   scope, so <c>glass</c>'s <c>GL</c> code is a placeholder rather than a
///   considered mapping.</item>
/// </list>
///
/// Unlike the previous random-suffix format, this id is fully deterministic
/// for a given payload/year, so the old "regenerate with a fresh random
/// suffix" collision strategy no longer converges. On collision the
/// generator instead replaces the final character with an attempt-based
/// disambiguator, mirroring <see cref="Core.ApplicationReferenceGenerator"/>'s
/// retry behaviour. Uniqueness is still enforced by a pre-flight lookup
/// against the persisted work item collection; if <see cref="MaxAttempts"/>
/// consecutive collisions occur an <see cref="InvalidOperationException"/>
/// is thrown so the calling approval service can surface the failure to the
/// operator.
/// </summary>
internal sealed class AccreditationIdGenerator(
    IAccreditationIdLookup lookup,
    INationResolver nationResolver,
    ILogger<AccreditationIdGenerator>? logger = null) : IAccreditationIdGenerator
{
    internal const int MaxAttempts = 5;
    private const int OrgIdWidth = 6;
    private const int PostcodeSuffixWidth = 3;
    private const string MaterialFallback = "XX";

    private readonly ILogger<AccreditationIdGenerator> _logger =
        logger ?? NullLogger<AccreditationIdGenerator>.Instance;

    private static readonly Dictionary<string, string> s_materialCodes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["plastic"] = "PL",
            ["steel"] = "ST",
            ["wood"] = "WO",
            ["aluminium"] = "AL",
            ["glass"] = "GL",
        };

    public async Task<string> GenerateAsync(
        BsonDocument payload,
        int year,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var candidate = BuildCandidate(payload, year);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            // The format is fully deterministic, so a collision cannot be
            // resolved by regenerating the same candidate — swap the final
            // character for an attempt-based disambiguator instead, same
            // convention as ApplicationReferenceGenerator's retry loop.
            var attemptCandidate = attempt == 1
                ? candidate
                : candidate[..^1] + DisambiguatorChar(attempt);

            if (!await lookup.ExistsAsync(attemptCandidate, cancellationToken))
            {
                return attemptCandidate;
            }
        }

        throw new InvalidOperationException(
            $"Unable to generate a unique accreditation id after {MaxAttempts} attempts.");
    }

    private string BuildCandidate(BsonDocument payload, int year)
    {
        var yearSegment = (year % 100).ToString("D2", CultureInfo.InvariantCulture);
        var postcode = ResolveRegulatorPostcode(payload);
        var agency = ResolveAgencyCode(postcode);
        var operatorType = ResolveOperatorType(payload);
        var orgId = FixedWidth(GetString(payload, "operatorOrganisationId"), OrgIdWidth);
        var postcodeSuffix = FixedWidth(CompactPostcode(postcode), PostcodeSuffixWidth);
        var material = ResolveMaterialCode(GetString(payload, "material"));

        return $"A{yearSegment}{agency}{operatorType}{orgId}{postcodeSuffix}{material}"
            .ToUpperInvariant();
    }

    private static char DisambiguatorChar(int attempt) => (char)('0' + (attempt % 10));

    private string ResolveAgencyCode(string? postcode) =>
        nationResolver.Resolve(postcode) switch
        {
            Nation.Scotland => "S",
            Nation.Wales => "W",
            Nation.NorthernIreland => "N",
            _ => "E",
        };

    private static string ResolveOperatorType(BsonDocument payload)
    {
        var wasteProcessingType = GetString(payload, "wasteProcessingType");
        return string.Equals(wasteProcessingType, "exporter", StringComparison.OrdinalIgnoreCase)
            ? "X"
            : "R";
    }

    private static string ResolveMaterialCode(string? material)
    {
        if (string.IsNullOrWhiteSpace(material))
        {
            return MaterialFallback;
        }

        return s_materialCodes.TryGetValue(material, out var code) ? code : MaterialFallback;
    }

    private static string FixedWidth(string? value, int width)
    {
        var normalised = (value ?? string.Empty).ToUpperInvariant();
        return normalised.Length >= width ? normalised[^width..] : normalised.PadLeft(width, '0');
    }

    private static string? CompactPostcode(string? postcode) =>
        string.IsNullOrWhiteSpace(postcode) ? postcode : postcode.Replace(" ", string.Empty);

    private static string? GetString(BsonDocument payload, string key) =>
        payload.TryGetValue(key, out var value) && value.IsString ? value.AsString : null;

    // Duplicated from Core.ApplicationReferenceGenerator rather than shared:
    // that generator lives in the Core layer so it stays usable by any
    // future work item type, and must not take a dependency back onto this
    // module. See its class summary for the RA-314 AC01/AC02 rationale
    // (Exporter uses registered office postcode, Reprocessor uses site
    // postcode) that this mirrors.
    private string? ResolveRegulatorPostcode(BsonDocument payload)
    {
        var wasteProcessingType = GetString(payload, "wasteProcessingType");
        var isExporter = string.Equals(wasteProcessingType, "exporter", StringComparison.OrdinalIgnoreCase);

        if (!isExporter)
        {
            return ExtractSitePostcode(payload);
        }

        var registeredOfficePostcode = GetString(payload, "companyRegisterAddressPostcode");
        if (string.IsNullOrWhiteSpace(registeredOfficePostcode))
        {
            _logger.LogWarning(
                "Exporter payload for operatorOrganisationId {OperatorOrganisationId} has no " +
                "companyRegisterAddressPostcode; falling open to the default England agency code " +
                "for the accreditation id.",
                GetString(payload, "operatorOrganisationId"));
        }

        return registeredOfficePostcode;
    }

    private static string? ExtractSitePostcode(BsonDocument payload)
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
}
