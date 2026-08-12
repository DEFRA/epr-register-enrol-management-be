using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-291: the six application sections a case worker may raise a query
/// against. Closed set — the case management frontend renders exactly these
/// checkboxes, and the backend refuses anything else so a hand-crafted POST
/// cannot smuggle an arbitrary string into the audit log.
/// </summary>
internal static class ReAccreditationQuerySections
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "authority-to-issue",
        "business-plan",
        "prn-tonnage",
        "sampling-and-inspection-plan",
        "broadly-equivalent-standards",
        "overseas-reprocessing-sites",
    };
}

/// <summary>
/// RA-291 query-reason word counter. The 200-word cap is a shared
/// contract with the case management frontend, so the counting rule has to
/// match it exactly: trim the string, split on any run of whitespace, and
/// count the non-empty tokens. Passing <c>null</c> as the separator array to
/// <see cref="string.Split(char[], StringSplitOptions)"/> is the BCL's
/// "split on Unicode whitespace" mode, which is the same partition a
/// <c>\s+</c> split produces.
/// </summary>
internal static class QueryReasonWordCounter
{
    public static int CountWords(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return 0;
        }

        return reason.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }
}

/// <summary>
/// RA-291 request validation for the bespoke query endpoint. Returns the
/// human-readable failure detail, or <c>null</c> when the request is valid.
/// Kept separate from <see cref="ReAccreditationQueryService"/> so the rules
/// are unit-testable without a persistence or engine substitute, and mirrors
/// the inline validation the sibling decision-rationale endpoint performs.
/// </summary>
internal static class ReAccreditationQueryValidator
{
    /// <summary>
    /// Maximum query length in words. Mirrors the frontend character-count
    /// component's limit; see <see cref="QueryReasonWordCounter"/> for the
    /// counting rule the two sides share.
    /// </summary>
    public const int MaxReasonWords = 200;

    // Wording is shared with the case management frontend's own validation so
    // the two spellings of the same rule cannot drift. The frontend validates
    // first; this is the backstop for hand-crafted requests.
    public const string NoSectionsMessage = "Select which areas you want to query";
    public const string UnknownSectionMessage = "Select a valid section to query";
    public const string MissingReasonMessage = "Enter a reason for the query";
    public const string ReasonTooLongMessage = "Query must be 200 words or fewer";

    public static string? Validate(QueryApplicationRequest? request)
    {
        var sections = request?.Sections;
        if (sections is null || sections.Count == 0)
        {
            return NoSectionsMessage;
        }

        foreach (var section in sections)
        {
            if (section is null || !ReAccreditationQuerySections.All.Contains(section))
            {
                // Deliberately does not echo the offending value back to the
                // caller: it is unvalidated client input and the frontend
                // only ever renders its own fixed labels.
                return UnknownSectionMessage;
            }
        }

        if (string.IsNullOrWhiteSpace(request!.Reason))
        {
            return MissingReasonMessage;
        }

        if (QueryReasonWordCounter.CountWords(request.Reason) > MaxReasonWords)
        {
            return ReasonTooLongMessage;
        }

        return null;
    }
}
