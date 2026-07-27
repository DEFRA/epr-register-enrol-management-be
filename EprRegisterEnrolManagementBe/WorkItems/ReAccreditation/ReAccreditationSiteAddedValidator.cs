using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-294/RA-297 request validation for the bespoke site-added notification
/// endpoint. Returns the human-readable failure detail, or <c>null</c> when
/// the request is valid. Mirrors <see cref="ReAccreditationQueryValidator"/>:
/// kept separate from <see cref="ReAccreditationSiteAddedService"/> so the
/// rules are unit-testable without a persistence or audit-appender
/// substitute.
/// </summary>
internal static class ReAccreditationSiteAddedValidator
{
    public const string InvalidSiteTypeMessage = "siteType must be 'ors' or 'interim'";
    public const string MissingOrsIdMessage = "orsId is required";
    public const string MissingSiteNumberMessage =
        "siteNumber is required when siteType is 'interim'";

    public static string? Validate(SiteAddedRequest? request)
    {
        var siteType = request?.SiteType;
        if (
            !string.Equals(siteType, "ors", StringComparison.Ordinal)
            && !string.Equals(siteType, "interim", StringComparison.Ordinal)
        )
        {
            return InvalidSiteTypeMessage;
        }

        if (string.IsNullOrWhiteSpace(request!.OrsId))
        {
            return MissingOrsIdMessage;
        }

        // An interim site is a waste staging point linked 1:1 to an ORS, so
        // it carries its own siteNumber in addition to the orsId it is
        // linked to. An ORS entry has no siteNumber of its own.
        if (
            string.Equals(siteType, "interim", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(request.SiteNumber)
        )
        {
            return MissingSiteNumberMessage;
        }

        return null;
    }
}
