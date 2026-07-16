namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx.Dtos;

internal sealed class ReExOrganisationDto
{
    public List<ReExRegistrationDto> Registrations { get; init; } = [];
    public List<ReExAccreditationDto> Accreditations { get; init; } = [];
}

internal sealed class ReExRegistrationDto
{
    public string? Id { get; init; }
    public string? AccreditationId { get; init; }
}

internal sealed class ReExAccreditationDto
{
    public string? Id { get; init; }
    public string? ValidFrom { get; init; }
    public ReExPrnIssuanceDto? PrnIssuance { get; init; }
}

internal sealed class ReExPrnIssuanceDto
{
    public string? TonnageBand { get; init; }
    public List<ReExSignatoryDto> Signatories { get; init; } = [];
    public List<ReExIncomeBusinessPlanItemDto> IncomeBusinessPlan { get; init; } = [];
}

internal sealed class ReExSignatoryDto
{
    public string? FullName { get; init; }
    public string? Email { get; init; }
}

internal sealed class ReExIncomeBusinessPlanItemDto
{
    public string? UsageDescription { get; init; }
    public int? PercentIncomeSpent { get; init; }
}
