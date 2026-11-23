namespace EprRegisterEnrolManagementBe.Integrations.OperatorBackend;

/// <summary>
/// RA-769: reads an accreditation as it stands today from the operator backend, for an In-Year Change case's
/// Accreditation summary tab. Read-only, and live on every call: nothing is copied into a case, so approving one
/// change never leaves another open case on the same accreditation out of date.
///
/// Like <see cref="IAccreditationNumberAdapter"/> it never throws its way out: the caller inspects the
/// <see cref="AccreditationSummaryReadOutcome"/>.
/// </summary>
public interface IAccreditationSummaryAdapter
{
    /// <summary>
    /// Calls <c>GET api/v1/accreditation-applications/case-management/{organisationId}/{applicationId}/accreditation-summary</c>
    /// on the operator backend.
    /// </summary>
    Task<AccreditationSummaryReadResult> GetAccreditationSummaryAsync(
        AccreditationSummaryReadRequest request,
        CancellationToken cancellationToken = default
    );
}

/// <param name="OrganisationId">The operator backend's organisation id, from the work item payload's
/// <c>operatorOrganisationId</c>.</param>
/// <param name="ApplicationId">The accreditation's application id, from the payload's
/// <c>accreditationApplicationId</c>.</param>
/// <param name="CorrelationId">Forwarded as <c>X-Correlation-Id</c>, so both services' logs for one read can be
/// joined.</param>
public sealed record AccreditationSummaryReadRequest(
    string OrganisationId,
    string ApplicationId,
    Guid CorrelationId
);

/// <summary>What a read came to.</summary>
public enum AccreditationSummaryReadOutcome
{
    Success,

    /// <summary>The operator backend holds no Approved accreditation at that address.</summary>
    NotFound,

    /// <summary>
    /// Anything else that stopped a usable answer: the integration is off or unconfigured, a transport failure, a
    /// 5xx after retries, an unexpected status, or a body that is not a usable summary.
    /// </summary>
    TransientFailure,
}

public sealed record AccreditationSummaryReadResult(
    AccreditationSummaryReadOutcome Outcome,
    OperatorAccreditationSummary? Summary,
    string? Message
)
{
    public static AccreditationSummaryReadResult Success(OperatorAccreditationSummary summary) =>
        new(AccreditationSummaryReadOutcome.Success, summary, null);

    public static AccreditationSummaryReadResult NotFound() =>
        new(AccreditationSummaryReadOutcome.NotFound, null, "No Approved accreditation.");

    public static AccreditationSummaryReadResult TransientFailure(string message) =>
        new(AccreditationSummaryReadOutcome.TransientFailure, null, message);
}

/// <summary>
/// The operator backend's accreditation summary, as it answers (camelCase). Shaped like the payload an application
/// is submitted with, so management-fe reads it with the code it already has for the Application summary tab.
/// <see cref="AccreditationYear"/> and <see cref="Material"/> are required: a body without them is not a usable
/// summary.
/// </summary>
public sealed record OperatorAccreditationSummary(
    string? OrganisationName,
    int? OrganisationNumber,
    string? AccreditationNumber,
    string? RegistrationNumber,
    int AccreditationYear,
    string? WasteProcessingType,
    string Material,
    string? GlassRecyclingProcess,
    string? SiteAddress,
    string? CompanyRegisteredAddress,
    OperatorSummaryPrns? Prns = null,
    OperatorSummaryBusinessPlan? BusinessPlan = null,
    IReadOnlyList<OperatorSummarySite>? OverseasSites = null
)
{
    /// <summary>Whether this is a usable summary: it has what the tab cannot do without. Not on any wire.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsUsable => AccreditationYear > 0 && !string.IsNullOrWhiteSpace(Material);
}

/// <summary>
/// The PRN details of the summary: the planned tonnage band (the enum's name, e.g. <c>UpTo5000</c>) and the
/// authorisers. Shaped like the payload's <c>prns</c>, so management-fe reads it with the code it already has.
/// </summary>
public sealed record OperatorSummaryPrns(
    string? PlannedTonnageBand,
    IReadOnlyList<OperatorSummaryAuthoriser> Authorisers
);

/// <summary>An authoriser by name and email. The operator backend never says which are new.</summary>
public sealed record OperatorSummaryAuthoriser(string? FullName, string? Email);

/// <summary>
/// The business plan of the summary: each category's share of PRN income and narrative. Shaped like the payload's
/// <c>businessPlan</c>.
/// </summary>
public sealed record OperatorSummaryBusinessPlan
{
    public int? NewInfrastructurePercent { get; init; }
    public int? PriceSupportPercent { get; init; }
    public int? BusinessCollectionsPercent { get; init; }
    public int? CommunicationsPercent { get; init; }
    public int? NewMarketsPercent { get; init; }
    public int? NewUsesPercent { get; init; }
    public int? OtherPercent { get; init; }
    public string? NewInfrastructureDetail { get; init; }
    public string? PriceSupportDetail { get; init; }
    public string? BusinessCollectionsDetail { get; init; }
    public string? CommunicationsDetail { get; init; }
    public string? NewMarketsDetail { get; init; }
    public string? NewUsesDetail { get; init; }
    public string? OtherDetail { get; init; }
}

/// <summary>
/// An overseas reprocessing site on the accreditation, shaped like the payload's site so management-fe reads it with
/// the code it already has. Only selected sites are answered, in ORS id then site id order. The operator backend
/// never says which sites are new (a site stays "new" for the year after grant), so none is marked. Its interim
/// sites are the active ones only. BES evidence files are added by a later task.
/// </summary>
public sealed record OperatorSummarySite(
    int SiteId,
    string? OrsId,
    string? SiteName,
    string? SiteAddress,
    string? AddressLine1,
    string? AddressLine2,
    string? TownOrCity,
    string? Country,
    string? Coordinates,
    string? ContactName,
    string? ContactEmail,
    string? ContactPhone,
    IReadOnlyList<string>? OperationCodes,
    string? Code1,
    string? Code2,
    string? Code3,
    string? RepatriatedLoads,
    bool? ConditionsOfExport,
    bool IsEu,
    bool IsOecd,
    IReadOnlyList<OperatorSummaryInterimSite>? InterimSites = null
);

/// <summary>
/// An active interim site of an overseas site, shaped like the payload's interim site. The operator backend never
/// returns a withdrawn one, nor says which are new.
/// </summary>
public sealed record OperatorSummaryInterimSite(
    int SiteId,
    string? SiteNumber,
    string? SiteName,
    string? AddressLine1,
    string? AddressLine2,
    string? TownOrCity,
    string? StateOrRegion,
    string? Postcode,
    string? Country,
    string? ContactName,
    string? ContactEmail,
    string? ContactPhone,
    IReadOnlyList<string>? OperationCodes
);
