using System.Globalization;
using System.Text.Json;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

/// <summary>
/// RA-768: one overseas site named by its operator-backend ids. In a request,
/// at least one id is set; matching uses <see cref="OrsId"/> when it is set and
/// <see cref="SiteId"/> only when it is not. In a response or audit entry,
/// <c>removed</c> carries the stored site's ids and <c>alreadyAbsent</c> echoes
/// the request entry. An absent id is <c>null</c>, never an empty string.
/// </summary>
internal sealed record OverseasSiteReference(string? OrsId, string? SiteId);

/// <summary>
/// RA-768 request body for
/// <c>POST /work-items/re-accreditation/{id}/overseas-sites/remove</c>.
/// The handler parses the body itself with
/// <see cref="RemoveOverseasSitesRequestParser"/>, so that every bad value is a
/// ProblemDetails 400 naming the field and is logged. This record documents the
/// shape in OpenAPI and carries the parsed values.
/// </summary>
internal sealed record RemoveOverseasSitesRequest(
    IReadOnlyList<OverseasSiteReference> Sites,
    string Reason
);

/// <summary>RA-768 response body of the overseas-sites removal endpoint.</summary>
internal sealed record RemoveOverseasSitesResponse(
    IReadOnlyList<OverseasSiteReference> Removed,
    IReadOnlyList<OverseasSiteReference> AlreadyAbsent
);

/// <summary>
/// RA-768: validates a <see cref="RemoveOverseasSitesRequest"/> body.
/// <c>sites</c> is a non-empty array. Each entry is an object with a non-blank
/// <c>orsId</c> or <c>siteId</c>. <c>reason</c> is a non-blank string. The
/// operator backend stores <c>siteId</c> as a number
/// (<c>OverseasSiteModel.SiteId</c> is an int), so a JSON integer is accepted
/// as well as a string, and both become the same string form.
/// </summary>
internal static class RemoveOverseasSitesRequestParser
{
    public const string SitesField = "sites";
    public const string ReasonField = "reason";

    /// <summary>
    /// Returns the parsed request, or null with <paramref name="field"/> and
    /// <paramref name="error"/> describing the first problem found.
    /// </summary>
    public static RemoveOverseasSitesRequest? Parse(
        JsonElement root,
        out string? field,
        out string? error
    )
    {
        field = null;
        error = null;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return Fail("body", "The request body must be a JSON object.", out field, out error);
        }

        if (
            !root.TryGetProperty(SitesField, out var sitesElement)
            || sitesElement.ValueKind != JsonValueKind.Array
            || sitesElement.GetArrayLength() == 0
        )
        {
            return Fail(
                SitesField,
                "sites is required and must be a non-empty array.",
                out field,
                out error
            );
        }

        var sites = new List<OverseasSiteReference>(sitesElement.GetArrayLength());
        var index = 0;
        foreach (var entry in sitesElement.EnumerateArray())
        {
            var entryField = $"{SitesField}[{index}]";
            if (entry.ValueKind != JsonValueKind.Object)
            {
                return Fail(entryField, $"{entryField} must be an object.", out field, out error);
            }

            if (!TryReadId(entry, "orsId", allowNumber: false, out var orsId))
            {
                return Fail(
                    $"{entryField}.orsId",
                    $"{entryField}.orsId must be a string or null.",
                    out field,
                    out error
                );
            }

            if (!TryReadId(entry, "siteId", allowNumber: true, out var siteId))
            {
                return Fail(
                    $"{entryField}.siteId",
                    $"{entryField}.siteId must be a string, an integer or null.",
                    out field,
                    out error
                );
            }

            if (orsId is null && siteId is null)
            {
                return Fail(
                    entryField,
                    $"{entryField} must have an orsId or a siteId.",
                    out field,
                    out error
                );
            }

            sites.Add(new OverseasSiteReference(orsId, siteId));
            index++;
        }

        if (
            !root.TryGetProperty(ReasonField, out var reasonElement)
            || reasonElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(reasonElement.GetString())
        )
        {
            return Fail(
                ReasonField,
                "reason is required and must be a non-empty string.",
                out field,
                out error
            );
        }

        return new RemoveOverseasSitesRequest(sites, reasonElement.GetString()!.Trim());
    }

    /// <summary>
    /// Reads an optional id. Returns false when the value has the wrong type.
    /// An absent, null or blank value reads as <c>null</c>: the entry then has
    /// no such id, and the caller rejects an entry that has neither.
    /// </summary>
    private static bool TryReadId(
        JsonElement entry,
        string propertyName,
        bool allowNumber,
        out string? value
    )
    {
        value = null;
        if (!entry.TryGetProperty(propertyName, out var element))
        {
            return true;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.String:
                var text = element.GetString();
                value = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                return true;
            case JsonValueKind.Number when allowNumber && element.TryGetInt64(out var number):
                value = number.ToString(CultureInfo.InvariantCulture);
                return true;
            default:
                return false;
        }
    }

    private static RemoveOverseasSitesRequest? Fail(
        string failedField,
        string message,
        out string? field,
        out string? error
    )
    {
        field = failedField;
        error = message;
        return null;
    }
}
