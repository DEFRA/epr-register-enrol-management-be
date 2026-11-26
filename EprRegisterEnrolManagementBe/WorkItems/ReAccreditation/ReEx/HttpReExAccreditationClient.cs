using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

    private readonly HttpClient _httpClient;
    private readonly IStructuredLogger<HttpReExAccreditationClient> _log;

    public HttpReExAccreditationClient(
        HttpClient httpClient,
        IOptions<ReExAccreditationConfig> config,
        IStructuredLogger<HttpReExAccreditationClient> log
    )
    {
        _log = log;
        var baseUrl = config.Value.BaseUrl;
        if (!string.IsNullOrWhiteSpace(baseUrl))
            httpClient.BaseAddress = new Uri(baseUrl.TrimEnd('/') + '/');
        _httpClient = httpClient;
    }

    public async Task<PriorYearAccreditationDto?> GetPriorYearAsync(
        string? organisationId,
        string? registrationId,
        int year,
        CancellationToken cancellationToken = default
    )
    {
        if (
            string.IsNullOrWhiteSpace(organisationId)
            || string.IsNullOrWhiteSpace(registrationId)
        )
        {
            return null;
        }

        var org = await FetchOrganisationAsync(organisationId, cancellationToken);
        if (org is null)
            return null;

        var registration = org.Registrations.FirstOrDefault(r => r.Id == registrationId);
        if (registration is null)
            return null;

        var accreditation = org.Accreditations.FirstOrDefault(a =>
            a.Id is not null && a.Id == registration.AccreditationId
        );
        if (accreditation is null)
            return null;

        if (
            !DateOnly.TryParse(
                accreditation.ValidFrom,
                CultureInfo.InvariantCulture,
                out var validFrom
            )
            || validFrom.Year != year
        )
        {
            return null;
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

        return new PriorYearAccreditationDto
        {
            Year = year,
            TonnageBand = tonnageBand,
            Authorisers = authorisers,
            BusinessPlan = businessPlan,
        };
    }

    public async Task<Nation?> GetNationAsync(
        string? organisationId,
        string? registrationId,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(organisationId) || string.IsNullOrWhiteSpace(registrationId))
        {
            return null;
        }

        // The RA-526 nation migration records a failed lookup per candidate
        // (CorrectionOutcome.LookupFailed) and leaves the item untouched, so it
        // keeps its null-on-failure contract; FetchOrganisationAsync has already
        // logged the cause at Error.
        ReExOrganisationDto? org;
        try
        {
            org = await FetchOrganisationAsync(organisationId, cancellationToken);
        }
        catch (ReExUnavailableException)
        {
            return null;
        }
        if (org is null)
            return null;

        var registration = org.Registrations.FirstOrDefault(r => r.Id == registrationId);
        if (registration is null)
            return null;

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

        return nation;
    }

    // Extracted from GetPriorYearAsync (S3776: cognitive complexity) - also keeps the
    // RA-475 auth-vs-data-gap distinction self-contained rather than nested inside the
    // caller's own branching.
    //
    // RA-752: returns null only when ReEx answers 404 (it has no such organisation).
    // Every other failure throws ReExUnavailableException after logging at Error, so a
    // caller can tell "no prior-year record" (404 from this service) from "ReEx is
    // unavailable" (502). management-fe still omits the prior-year section in both
    // cases; the Admin UI shows an unavailable banner for the second.
    private async Task<ReExOrganisationDto?> FetchOrganisationAsync(
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
            // HttpClient timeout, not the caller giving up.
            _log.Log(
                LogLevel.Error,
                "ReEx prior-year request failed",
                new Dictionary<string, object?> { [OrganisationIdLogField] = organisationId },
                exception: ex
            );
            throw new ReExUnavailableException("ReEx request failed.", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _log.Log(
                    LogLevel.Warning,
                    "ReEx has no record of the organisation",
                    new Dictionary<string, object?>
                    {
                        [OrganisationIdLogField] = organisationId,
                        ["reex.status_code"] = (int)response.StatusCode,
                    }
                );
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                // RA-475: a 401/403 is never a data condition — it means this
                // service's ReEx Basic-auth credentials (REEX_API_BASIC_AUTH_USERNAME /
                // REEX_API_BASIC_AUTH_PASSWORD) or ReExApi:BaseUrl are wrong for the
                // environment, and EVERY prior-year lookup will fail for as long as
                // they are. Named as a credentials problem so it is distinguishable
                // from any other ReEx failure.
                var isAuthFailure =
                    response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

                _log.Log(
                    LogLevel.Error,
                    isAuthFailure
                        ? "ReEx rejected this service's credentials; prior-year lookup is failing for "
                            + "every application until REEX_API_BASIC_AUTH_* / ReExApi:BaseUrl are corrected"
                        : "ReEx prior-year lookup failed",
                    new Dictionary<string, object?>
                    {
                        [OrganisationIdLogField] = organisationId,
                        ["reex.status_code"] = (int)response.StatusCode,
                    }
                );
                throw new ReExUnavailableException(
                    $"ReEx returned {(int)response.StatusCode}."
                );
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
                throw new ReExUnavailableException("ReEx returned an unreadable organisation.", ex);
            }

            if (organisation is null)
            {
                LogUnreadableBody(organisationId, null);
                throw new ReExUnavailableException("ReEx returned an empty organisation.");
            }

            return organisation;
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
