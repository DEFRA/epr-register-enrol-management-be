namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx.Dtos;

public sealed class PriorYearAccreditationDto
{
    public int Year { get; init; }
    public string? TonnageBand { get; init; }
    public List<PriorYearAuthoriserDto> Authorisers { get; init; } = [];
    public PriorYearBusinessPlanDto BusinessPlan { get; init; } = new();
}

public sealed class PriorYearAuthoriserDto
{
    public string FullName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
}

public sealed class PriorYearBusinessPlanDto
{
    public int? NewInfrastructurePercent { get; set; }
    public int? PriceSupportPercent { get; set; }
    public int? BusinessCollectionsPercent { get; set; }
    public int? CommunicationsPercent { get; set; }
    public int? NewMarketsPercent { get; set; }
    public int? NewUsesPercent { get; set; }
    public int? OtherPercent { get; set; }
}
