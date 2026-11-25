using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.AspNetCore.Http;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Translates the query string of <c>GET /work-items</c> into a
/// <see cref="WorkItemQuery"/>. Lifted to a static helper so the endpoint
/// stays tight and can be unit-tested in isolation.
/// </summary>
internal static class WorkItemQueryBinding
{
    internal const string TypeIdParam = "typeId";
    // RA-739. Multi-value; expanded to typeIds by WorkItemQuery.ResolveTypeIds in persistence.
    internal const string CategoryParam = "category";
    internal const string StateIdParam = "stateId";
    internal const string SearchParam = "search";
    internal const string AssigneeIdParam = "assigneeId";
    internal const string UnassignedOnlyParam = "unassigned";
    internal const string PageParam = "page";
    internal const string PageSizeParam = "pageSize";
    internal const string NationParam = "nation";
    internal const string WasteProcessingTypeParam = "wasteProcessingType";
    internal const string IncludeArchivedParam = "includeArchived";
    internal const string OrgIdParam = "orgId";
    internal const string RegistrationIdParam = "registrationId";
    internal const string OrgNameParam = "orgName";

    // RA-324 (Applications page) filter + sort params.
    internal const string MaterialParam = "material";
    internal const string OrganisationParam = "organisation";
    internal const string SortParam = "sort";
    internal const string SortDirectionParam = "dir";

    internal const string SubmittedByParam = "submittedBy";

    // RA-751 (Admin UI work item search). Strictly parsed: see Validate.
    internal const string QParam = "q";
    internal const string AssignedParam = "assigned";
    internal const string ArchivedParam = "archived";

    public static WorkItemQuery FromQueryString(IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(query);

        return new WorkItemQuery(
            TypeIds: ReadStrings(query, TypeIdParam),
            StateIds: ReadStrings(query, StateIdParam),
            Search: ReadString(query, SearchParam),
            AssigneeId: ReadString(query, AssigneeIdParam),
            UnassignedOnly: ReadBool(query, UnassignedOnlyParam),
            Page: ReadInt(query, PageParam, defaultValue: 1),
            PageSize: ReadInt(query, PageSizeParam, defaultValue: WorkItemQuery.DefaultPageSize),
            SubmittedBy: ReadString(query, SubmittedByParam),
            Nations: ReadNations(query, NationParam),
            WasteProcessingTypes: ReadStrings(query, WasteProcessingTypeParam),
            IncludeArchived: ReadBool(query, IncludeArchivedParam),
            OrgId: ReadString(query, OrgIdParam),
            RegistrationId: ReadString(query, RegistrationIdParam),
            OrgName: ReadString(query, OrgNameParam),
            Materials: ReadStrings(query, MaterialParam),
            Organisation: ReadString(query, OrganisationParam),
            Sort: ReadString(query, SortParam),
            SortDescending: ReadSortDirection(query, SortDirectionParam),
            Categories: ReadStrings(query, CategoryParam),
            Q: ReadString(query, QParam),
            Assigned: ReadStrictBool(query, AssignedParam).Value,
            Archived: ReadStrictBool(query, ArchivedParam).Value);
    }

    /// <summary>
    /// RA-751: checks the strictly parsed parameters. Returns the problems
    /// keyed by parameter name (empty when there are none), so the endpoint
    /// can answer 400 instead of quietly dropping a filter the caller asked
    /// for. <c>assigned</c> and <c>archived</c> must be exactly <c>true</c> or
    /// <c>false</c> (any case) when present, and <c>assigned</c> cannot be
    /// combined with <c>unassigned</c> or <c>assigneeId</c>, whose meaning it
    /// would contradict or blur.
    /// </summary>
    public static Dictionary<string, string[]> Validate(IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var key in new[] { AssignedParam, ArchivedParam })
        {
            if (!ReadStrictBool(query, key).IsValid)
            {
                errors[key] = [$"'{key}' must be 'true' or 'false'."];
            }
        }

        if (
            query.ContainsKey(AssignedParam)
            && (query.ContainsKey(UnassignedOnlyParam) || query.ContainsKey(AssigneeIdParam))
        )
        {
            errors[AssignedParam] =
            [
                .. errors.GetValueOrDefault(AssignedParam, []),
                $"'{AssignedParam}' cannot be combined with '{UnassignedOnlyParam}' or '{AssigneeIdParam}'.",
            ];
        }

        // RA-751: a non-blank sort token or direction must be one this
        // endpoint understands. Blank or absent keeps the default order.
        if (NonBlankValues(query, SortParam).Any(v => !WorkItemSort.IsKnownToken(v)))
        {
            errors[SortParam] =
            [
                $"'{SortParam}' must be one of: {string.Join(", ", WorkItemSort.Tokens.Order(StringComparer.Ordinal))}.",
            ];
        }
        if (NonBlankValues(query, SortDirectionParam).Any(v => ParseSortDirection(v) is null))
        {
            errors[SortDirectionParam] = [$"'{SortDirectionParam}' must be 'asc' or 'desc'."];
        }

        return errors;
    }

    private static IEnumerable<string> NonBlankValues(IQueryCollection query, string key) =>
        query.TryGetValue(key, out var values)
            ? values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!)
            : [];

    /// <summary>
    /// Absent → valid, no value. Exactly one value of <c>true</c> or
    /// <c>false</c> (any case, surrounding whitespace ignored) → valid, that
    /// value. Anything else, including an empty value or a repeated key, is
    /// invalid.
    /// </summary>
    private static (bool IsValid, bool? Value) ReadStrictBool(IQueryCollection query, string key)
    {
        if (!query.TryGetValue(key, out var values))
        {
            return (true, null);
        }
        if (values.Count != 1)
        {
            return (false, null);
        }

        return bool.TryParse(values[0]?.Trim(), out var parsed) ? (true, parsed) : (false, null);
    }

    /// <summary>
    /// Reads the optional <c>?dir=</c> value: <c>desc</c>/<c>descending</c> →
    /// <c>true</c>, <c>asc</c>/<c>ascending</c> → <c>false</c>, absent or blank
    /// → <c>null</c> so the requested sort column applies its own natural
    /// default direction. An unrecognised value is rejected with 400 by
    /// <see cref="Validate"/> before binding (RA-751).
    /// </summary>
    private static bool? ReadSortDirection(IQueryCollection query, string key)
    {
        var value = ReadString(query, key);
        return string.IsNullOrWhiteSpace(value) ? null : ParseSortDirection(value);
    }

    /// <summary>
    /// <c>desc</c>/<c>descending</c> → <c>true</c>, <c>asc</c>/<c>ascending</c>
    /// → <c>false</c> (any case, surrounding whitespace ignored); anything else
    /// → <c>null</c>, which <see cref="Validate"/> rejects.
    /// </summary>
    private static bool? ParseSortDirection(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Equals("desc", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("descending", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (trimmed.Equals("asc", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("ascending", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return null;
    }

    private static IReadOnlyCollection<string>? ReadStrings(IQueryCollection query, string key)
    {
        if (!query.TryGetValue(key, out var values) || values.Count == 0)
        {
            return null;
        }

        var trimmed = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return trimmed.Count == 0 ? null : trimmed;
    }

    private static string? ReadString(IQueryCollection query, string key)
    {
        if (!query.TryGetValue(key, out var values) || values.Count == 0)
        {
            return null;
        }

        var first = values[0];
        return string.IsNullOrWhiteSpace(first) ? null : first;
    }

    private static int ReadInt(IQueryCollection query, string key, int defaultValue)
    {
        if (!query.TryGetValue(key, out var values) || values.Count == 0)
        {
            return defaultValue;
        }

        return int.TryParse(values[0], out var parsed) ? parsed : defaultValue;
    }

    private static bool ReadBool(IQueryCollection query, string key)
    {
        if (!query.TryGetValue(key, out var values) || values.Count == 0)
        {
            return false;
        }

        var first = values[0];
        if (string.IsNullOrWhiteSpace(first))
        {
            return false;
        }

        // Accept the usual truthy spellings used in HTML forms / query
        // strings: "true", "1", "on", "yes" (any case).
        var trimmed = first.Trim();
        return trimmed.Equals("true", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("1", StringComparison.Ordinal)
            || trimmed.Equals("on", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads <c>?nation=...</c> values, normalising them to the canonical
    /// <see cref="Nation"/> enum member name (case-insensitive parse).
    /// Unknown / malformed values are dropped so they cannot bleed through to
    /// the persistence filter, where they would silently match nothing.
    /// Returns <c>null</c> when no valid values were supplied.
    /// </summary>
    private static IReadOnlyCollection<string>? ReadNations(IQueryCollection query, string key)
    {
        var raw = ReadStrings(query, key);
        if (raw is null)
        {
            return null;
        }

        var canonical = raw
            .Select(v => Enum.TryParse<Nation>(v, ignoreCase: true, out var parsed) ? parsed.ToString() : null)
            .Where(v => v is not null)
            .Distinct(StringComparer.Ordinal)
            .Cast<string>()
            .ToList();

        return canonical.Count == 0 ? null : canonical;
    }
}
