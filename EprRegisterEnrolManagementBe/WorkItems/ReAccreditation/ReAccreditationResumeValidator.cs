using System.Text.Json;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-311/MBE-1 request validation for <c>resume-from-query</c>. Returns the
/// human-readable failure detail, or <c>null</c> when the request is valid.
/// Mirrors <see cref="ReAccreditationQueryValidator"/>: kept separate from
/// <see cref="ReAccreditationResumeService"/> so the rules are unit-testable
/// without a persistence or engine substitute.
///
/// Deliberately does not validate <c>sections</c>/<c>fileReferences</c>
/// content or completeness — the plan is explicit that resubmission
/// completeness is not validated by this build; those fields are opaque to
/// this repo (see <see cref="ResumeFromQueryRequest"/>).
/// </summary>
internal static class ReAccreditationResumeValidator
{
    public const string NoSectionsMessage = "sectionKeys must contain at least one section";
    public const string UnknownSectionMessage = "sectionKeys must only contain valid sections";
    public const string MissingResponderMessage =
        "responderContactDetails.fullName, .email and .role are all required";
    public const string InvalidSectionValueMessage = "each entry in sections must be a JSON object";

    public static string? Validate(ResumeFromQueryRequest? request)
    {
        var sectionKeys = request?.SectionKeys;
        if (sectionKeys is null || sectionKeys.Count == 0)
        {
            return NoSectionsMessage;
        }

        foreach (var sectionKey in sectionKeys)
        {
            if (sectionKey is null || !ReAccreditationQuerySections.All.Contains(sectionKey))
            {
                // Deliberately does not echo the offending value back to the
                // caller, mirroring ReAccreditationQueryValidator.
                return UnknownSectionMessage;
            }
        }

        var responder = request!.ResponderContactDetails;
        if (string.IsNullOrWhiteSpace(responder?.FullName)
            || string.IsNullOrWhiteSpace(responder?.Email)
            || string.IsNullOrWhiteSpace(responder?.Role))
        {
            return MissingResponderMessage;
        }

        // WorkItemPayloadConverter.ToBson (used by the service to store each
        // section) requires a JSON object; checked here so a malformed
        // section value is a 400, not an unhandled exception.
        if (request.Sections is not null)
        {
            foreach (var (_, value) in request.Sections)
            {
                if (value.ValueKind != JsonValueKind.Object)
                {
                    return InvalidSectionValueMessage;
                }
            }
        }

        return null;
    }
}
