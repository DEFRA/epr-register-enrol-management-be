using System.Net.Http.Json;
using System.Text.Json;
using EprRegisterEnrolManagementBe.Utils.Logging;
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

    private static readonly Dictionary<string, Action<PriorYearBusinessPlanDto, int>> s_businessPlanMap =
        new(StringComparer.Ordinal)
        {
            ["New reprocessing infrastructure and maintaining existing infrastructure"]
                = (dto, v) => dto.NewInfrastructurePercent = v,
            ["Price support for buying packaging waste or selling recycled packaging waste"]
                = (dto, v) => dto.PriceSupportPercent = v,
            ["Support for business collections"]
                = (dto, v) => dto.BusinessCollectionsPercent = v,
            ["Communications, including information campaigns"]
                = (dto, v) => dto.CommunicationsPercent = v,
            ["Developing new markets for products made from recycled packaging waste"]
                = (dto, v) => dto.NewMarketsPercent = v,
            ["Developing new uses for recycled packaging waste"]
                = (dto, v) => dto.NewUsesPercent = v,
            ["Activities or investment not covered by the other categories"]
                = (dto, v) => dto.OtherPercent = v,
        };

    private readonly HttpClient _httpClient;
    private readonly IStructuredLogger<HttpReExAccreditationClient> _log;

    public HttpReExAccreditationClient(
        HttpClient httpClient,
        IOptions<ReExAccreditationConfig> config,
        IStructuredLogger<HttpReExAccreditationClient> log)
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
        int? year,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(organisationId) ||
            string.IsNullOrWhiteSpace(registrationId) ||
            year is null)
        {
            return null;
        }

        ReExOrganisationDto? org;
        try
        {
            using var response = await _httpClient.GetAsync(
                $"v1/organisations/{Uri.EscapeDataString(organisationId)}",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _log.Log(
                    LogLevel.Warning,
                    "ReEx prior-year lookup failed",
                    new Dictionary<string, object?>
                    {
                        ["reex.organisation_id"] = organisationId,
                        ["reex.status_code"] = (int)response.StatusCode
                    });
                return null;
            }

            org = await response.Content.ReadFromJsonAsync<ReExOrganisationDto>(s_jsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.Log(
                LogLevel.Error,
                "ReEx prior-year request failed",
                new Dictionary<string, object?> { ["reex.organisation_id"] = organisationId },
                exception: ex);
            return null;
        }

        if (org is null) return null;

        var registration = org.Registrations.FirstOrDefault(r => r.Id == registrationId);
        if (registration is null) return null;

        var accreditation = org.Accreditations
            .FirstOrDefault(a => a.Id is not null && a.Id == registration.AccreditationId);
        if (accreditation is null) return null;

        if (!DateOnly.TryParse(accreditation.ValidFrom, out var validFrom) ||
            validFrom.Year != year.Value)
        {
            return null;
        }

        var prn = accreditation.PrnIssuance;

        var authorisers = prn?.Signatories
            .Select(s => new PriorYearAuthoriserDto
            {
                FullName = s.FullName ?? string.Empty,
                Email = s.Email ?? string.Empty
            })
            .ToList() ?? [];

        var businessPlan = MapBusinessPlan(prn?.IncomeBusinessPlan ?? []);

        return new PriorYearAccreditationDto
        {
            Year = year.Value,
            TonnageBand = prn?.TonnageBand,
            Authorisers = authorisers,
            BusinessPlan = businessPlan
        };
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
                    new Dictionary<string, object?> { ["reex.usage_description"] = item.UsageDescription });
        }
        return dto;
    }
}
