using System.Diagnostics.CodeAnalysis;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Produces the human-facing work-item <c>applicationReference</c>
/// (RA-318). The backend owns reference generation so a client can never
/// supply, spoof or collide a reference.
///
/// Format (RA-318): <c>APP</c> + 2-digit accreditation year + 2-char
/// agency code (derived from the site postcode) + the operator
/// organisation id + the last 3 characters of the site postcode + the
/// first 2 characters of the material, all upper-cased. The result is
/// truncated to <see cref="MaxLength"/> characters because this value is
/// also used as a BACS payment reference. Deterministic for a given
/// payload — unlike the previous random-suffix format, the same
/// submission always yields the same reference.
/// </summary>
public interface IApplicationReferenceGenerator
{
    /// <summary>Generate a reference derived from the submission <paramref name="payload"/>.</summary>
    string Generate(BsonDocument payload);
}

/// <inheritdoc />
public sealed class ApplicationReferenceGenerator : IApplicationReferenceGenerator
{
    /// <summary>Literal prefix every reference carries.</summary>
    public const string Prefix = "APP";

    /// <summary>Maximum reference length — this value doubles as a BACS payment reference.</summary>
    public const int MaxLength = 18;

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

    public ApplicationReferenceGenerator(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Generate(BsonDocument payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var year = ResolveYear(payload);
        var postcode = GetString(payload, "siteAddressPostcode");
        var agency = ResolveAgencyCode(postcode);
        var organisationId = GetString(payload, "operatorOrganisationId") ?? string.Empty;
        var postcodeSuffix = PostcodeSuffix(postcode);
        var materialPrefix = MaterialPrefix(GetString(payload, "material"));

        var reference =
            $"{Prefix}{year:D2}{agency}{organisationId}{postcodeSuffix}{materialPrefix}".ToUpperInvariant();

        return reference.Length > MaxLength ? reference[..MaxLength] : reference;
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
