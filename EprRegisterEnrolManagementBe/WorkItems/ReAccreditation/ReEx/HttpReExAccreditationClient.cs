using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using EprRegisterEnrolManagementBe.Utils.Logging;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx.Dtos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx;

internal sealed class HttpReExAccreditationClient : IReExAccreditationClient
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowOutOfOrderMetadataProperties = true,
    };

    // ReEx returns tonnage band as a raw snake_case code (e.g. "up_to_500").
    // Mirrors epr-register-enrol-backend's HttpReExApiAdapter.TonnageBandMap so
    // prior-year data lines up with the same canonical values the current
    // year's PRNs section already uses (application-details.controller.js's
    // TONNAGE_BAND_LABELS keys off "UpTo500" etc) — without this mapping the
    // raw ReEx code renders unformatted on the Application details page.
    private static readonly Dictionary<string, string> s_tonnageBandMap = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["up_to_500"] = "UpTo500",
        ["up_to_5000"] = "UpTo5000",
        ["up_to_10000"] = "UpTo10000",
        ["over_10000"] = "Over10000",
    };

    private static readonly Dictionary<
        string,
        Action<PriorYearBusinessPlanDto, int>
    > s_businessPlanMap = new(StringComparer.Ordinal)
    {
        ["New reprocessing infrastructure and maintaining existing infrastructure"] = (dto, v) =>
            dto.NewInfrastructurePercent = v,
        ["Price support for buying packaging waste or selling recycled packaging waste"] = (
            dto,
            v
        ) => dto.PriceSupportPercent = v,
        ["Support for business collections"] = (dto, v) => dto.BusinessCollectionsPercent = v,
        ["Communications, including information campaigns"] = (dto, v) =>
            dto.CommunicationsPercent = v,
        ["Developing new markets for products made from recycled packaging waste"] = (dto, v) =>
            dto.NewMarketsPercent = v,
        ["Developing new uses for recycled packaging waste"] = (dto, v) => dto.NewUsesPercent = v,
        ["Activities or investment not covered by the other categories"] = (dto, v) =>
            dto.OtherPercent = v,
    };

    // Structured-log field naming the ReEx organisation a lookup was for.
    private const string OrganisationIdLogField = "reex.organisation_id";
    private const string StatusCodeLogField = "reex.status_code";

    /// <summary>
    /// How long one ReEx request may take before it counts as unavailable. Both
    /// front ends load prior-year on every work item page; management-fe gives
    /// up after 5000ms (<c>BACKEND_API_TIMEOUT_MS</c>) and shows nothing, so a
    /// ReEx that hangs must time out here first, be logged at Error and answer
    /// 502, rather than leave no trace once the caller has gone. 4s leaves
    /// room inside management-fe's 5s for the work item read before the call.
    /// Keep it under management-fe's timeout if either changes.
    /// </summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);

    // ReEx (DEFRA/epr-backend, 4b9fc27) answers an unknown organisation with a
    // Boom 404 whose message names the organisation:
    // src/registration-accreditation-admin/routes/get-by-id.js throws
    // "Organisation not found" and src/repositories/organisations/mongodb.js
    // "Organisation with id <id> not found". A 404 without that message (Hapi's
    // own route miss, "Not Found"; a proxy or gateway page; a wrong
    // ReExApi:BaseUrl path) is not ReEx saying "no such organisation".
    private static readonly Regex s_organisationNotFoundMessage =
        new(
            "^Organisation\\b.*\\bnot found$",
            RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100)
        );

    private readonly HttpClient _httpClient;
    private readonly IStructuredLogger<HttpReExAccreditationClient> _log;

    public HttpReExAccreditationClient(
        HttpClient httpClient,
        IOptions<ReExAccreditationConfig> config,
        IStructuredLogger<HttpReExAccreditationClient> log
    )
        : this(httpClient, config, log, RequestTimeout) { }

    // Tests only: a shorter timeout, so a test can drive the real HttpClient
    // timeout without waiting RequestTimeout. Internal, so DI never picks it.
    internal HttpReExAccreditationClient(
        HttpClient httpClient,
        IOptions<ReExAccreditationConfig> config,
        IStructuredLogger<HttpReExAccreditationClient> log,
        TimeSpan timeout
    )
    {
        _log = log;
        var baseUrl = config.Value.BaseUrl;
        if (!string.IsNullOrWhiteSpace(baseUrl))
            httpClient.BaseAddress = new Uri(baseUrl.TrimEnd('/') + '/');
        httpClient.Timeout = timeout;
        _httpClient = httpClient;
    }

    public async Task<PriorYearReadResult> GetPriorYearAsync(
        string organisationId,
        string registrationId,
        int year,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organisationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationId);

        var (outcome, org) = await FetchOrganisationAsync(organisationId, cancellationToken);
        if (org is null)
            return outcome == ReExReadOutcome.NotFound
                ? PriorYearReadResult.NotFound()
                : PriorYearReadResult.Unavailable();

        var registration = org.Registrations.FirstOrDefault(r => r.Id == registrationId);
        if (registration is null)
            return PriorYearReadResult.NotFound();

        var accreditation = org.Accreditations.FirstOrDefault(a =>
            a.Id is not null && a.Id == registration.AccreditationId
        );
        if (accreditation is null)
            return PriorYearReadResult.NotFound();

        if (
            !DateOnly.TryParse(
                accreditation.ValidFrom,
                CultureInfo.InvariantCulture,
                out var validFrom
            )
            || validFrom.Year != year
        )
        {
            return PriorYearReadResult.NotFound();
        }

        var prn = accreditation.PrnIssuance;

        var tonnageBand = MapTonnageBand(prn?.TonnageBand);

        var authorisers =
            prn?.Signatories.Select(s => new PriorYearAuthoriserDto
                {
                    FullName = s.FullName ?? string.Empty,
                    Email = s.Email ?? string.Empty,
                })
                .ToList()
            ?? [];

        var businessPlan = MapBusinessPlan(prn?.IncomeBusinessPlan ?? []);

        return PriorYearReadResult.Found(
            new PriorYearAccreditationDto
            {
                Year = year,
                TonnageBand = tonnageBand,
                Authorisers = authorisers,
                BusinessPlan = businessPlan,
            }
        );
    }

    public async Task<NationReadResult> GetNationAsync(
        string organisationId,
        string registrationId,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organisationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationId);

        var (outcome, org) = await FetchOrganisationAsync(organisationId, cancellationToken);
        if (org is null)
            return outcome == ReExReadOutcome.NotFound
                ? NationReadResult.NotFound()
                : NationReadResult.Unavailable();

        var registration = org.Registrations.FirstOrDefault(r => r.Id == registrationId);
        if (registration is null)
            return NationReadResult.NotFound();

        if (!RegulatorNationMapper.TryMap(registration.SubmittedToRegulator, out var nation))
        {
            _log.Log(
                LogLevel.Warning,
                "Unrecognised regulator code for ReEx registration",
                new Dictionary<string, object?>
                {
                    [OrganisationIdLogField] = organisationId,
                    ["reex.registration_id"] = registrationId,
                    ["reex.regulator_code"] = registration.SubmittedToRegulator,
                }
            );
        }

        return NationReadResult.Found(nation);
    }

    // The organisation, or why there is none: NotFound only for ReEx's own
    // "organisation not found" 404; anything else is logged at Error and is
    // Unavailable.
    private async Task<(ReExReadOutcome Outcome, ReExOrganisationDto? Organisation)> FetchOrganisationAsync(
        string organisationId,
        CancellationToken cancellationToken
    )
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(
                $"v1/organisations/{Uri.EscapeDataString(organisationId)}",
                cancellationToken
            );
        }
        catch (Exception ex)
            when ((ex is HttpRequestException or TaskCanceledException)
                && !cancellationToken.IsCancellationRequested)
        {
            // A TaskCanceledException without the caller's token cancelled is the
            // HttpClient timeout (RequestTimeout), not the caller giving up.
            _log.Log(
                LogLevel.Error,
                "ReEx organisation request failed",
                new Dictionary<string, object?> { [OrganisationIdLogField] = organisationId },
                exception: ex
            );
            return (ReExReadOutcome.Unavailable, null);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                return await ReadNotFoundAsync(response, organisationId, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // RA-475: a 401/403 is never a data condition — it means this
                // service's ReEx Basic-auth credentials (REEX_API_BASIC_AUTH_USERNAME /
                // REEX_API_BASIC_AUTH_PASSWORD) or ReExApi:BaseUrl are wrong for the
                // environment, and EVERY ReEx lookup will fail for as long as
                // they are. Named as a credentials problem so it is distinguishable
                // from any other ReEx failure.
                var isAuthFailure =
                    response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

                _log.Log(
                    LogLevel.Error,
                    isAuthFailure
                        ? "ReEx rejected this service's credentials; every ReEx lookup is failing "
                            + "until REEX_API_BASIC_AUTH_* / ReExApi:BaseUrl are corrected"
                        : "ReEx organisation lookup failed",
                    new Dictionary<string, object?>
                    {
                        [OrganisationIdLogField] = organisationId,
                        [StatusCodeLogField] = (int)response.StatusCode,
                    }
                );
                return (ReExReadOutcome.Unavailable, null);
            }

            ReExOrganisationDto? organisation;
            try
            {
                organisation = await response.Content.ReadFromJsonAsync<ReExOrganisationDto>(
                    s_jsonOptions,
                    cancellationToken
                );
            }
            catch (JsonException ex)
            {
                LogUnreadableBody(organisationId, ex);
                return (ReExReadOutcome.Unavailable, null);
            }

            if (organisation is null)
            {
                LogUnreadableBody(organisationId, null);
                return (ReExReadOutcome.Unavailable, null);
            }

            return (ReExReadOutcome.Found, organisation);
        }
    }

    // A 404 is ReEx saying "no such organisation" only when its body is ReEx's
    // own not-found error (see s_organisationNotFoundMessage). Any other 404 is
    // treated as ReEx being unreachable at the configured path, so an outage
    // never reads as "no prior year".
    private async Task<(ReExReadOutcome Outcome, ReExOrganisationDto? Organisation)> ReadNotFoundAsync(
        HttpResponseMessage response,
        string organisationId,
        CancellationToken cancellationToken
    )
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (IsOrganisationNotFoundBody(body))
        {
            _log.Log(
                LogLevel.Warning,
                "ReEx has no record of the organisation",
                new Dictionary<string, object?>
                {
                    [OrganisationIdLogField] = organisationId,
                    [StatusCodeLogField] = (int)response.StatusCode,
                }
            );
            return (ReExReadOutcome.NotFound, null);
        }

        _log.Log(
            LogLevel.Error,
            "ReEx answered 404 without its organisation-not-found error; check ReExApi:BaseUrl "
                + "and the CDP proxy route",
            new Dictionary<string, object?>
            {
                [OrganisationIdLogField] = organisationId,
                [StatusCodeLogField] = (int)response.StatusCode,
            }
        );
        return (ReExReadOutcome.Unavailable, null);
    }

    private static bool IsOrganisationNotFoundBody(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String
                && s_organisationNotFoundMessage.IsMatch(message.GetString()!.Trim());
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void LogUnreadableBody(string organisationId, Exception? exception) =>
        _log.Log(
            LogLevel.Error,
            "ReEx returned a body that is not an organisation",
            new Dictionary<string, object?> { [OrganisationIdLogField] = organisationId },
            exception: exception
        );

    private string? MapTonnageBand(string? rawTonnageBand)
    {
        if (rawTonnageBand is null)
            return null;

        if (s_tonnageBandMap.TryGetValue(rawTonnageBand, out var mapped))
            return mapped;

        _log.Log(
            LogLevel.Warning,
            "Unrecognised ReEx TonnageBand value",
            new Dictionary<string, object?> { ["reex.tonnage_band"] = rawTonnageBand }
        );
        return rawTonnageBand;
    }

    private PriorYearBusinessPlanDto MapBusinessPlan(List<ReExIncomeBusinessPlanItemDto> items)
    {
        var dto = new PriorYearBusinessPlanDto();
        foreach (var item in items)
        {
            if (item.UsageDescription is null || item.PercentIncomeSpent is null)
                continue;

            if (s_businessPlanMap.TryGetValue(item.UsageDescription, out var setter))
                setter(dto, item.PercentIncomeSpent.Value);
            else
                _log.Log(
                    LogLevel.Warning,
                    "Unrecognised ReEx business plan usage description",
                    new Dictionary<string, object?>
                    {
                        ["reex.usage_description"] = item.UsageDescription,
                    }
                );
        }
        return dto;
    }
}
