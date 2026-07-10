using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

public class ReAccreditationDecisionServiceTests
{
    private readonly ReAccreditationDecisionService _service = new();

    [Fact]
    public void Recommends_approve_when_compliance_history_within_tolerance()
    {
        var payload = new ReAccreditationPayload
        {
            OrganisationName = "Acme Recycling Ltd",
            RegistrationNumber = "EX-12345",
            Material = "plastic",
            PreviousAccreditationYear = 2024,
            ComplianceIssuesReported = 1
        };

        var recommendation = _service.EvaluateRecommendation(payload);

        Assert.Equal(ReAccreditationRecommendation.Approve, recommendation.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(recommendation.Rationale));
    }

    [Fact]
    public void Recommends_reject_when_compliance_issues_exceed_threshold()
    {
        var payload = new ReAccreditationPayload
        {
            OrganisationName = "Acme Recycling Ltd",
            RegistrationNumber = "EX-12345",
            Material = "plastic",
            PreviousAccreditationYear = 2024,
            ComplianceIssuesReported = ReAccreditationDecisionService.MaxToleratedComplianceIssues + 1
        };

        var recommendation = _service.EvaluateRecommendation(payload);

        Assert.Equal(ReAccreditationRecommendation.Reject, recommendation.Outcome);
        Assert.Contains("compliance issues", recommendation.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recommends_more_info_needed_when_required_fields_missing()
    {
        var payload = new ReAccreditationPayload
        {
            OrganisationName = "Acme Recycling Ltd",
            RegistrationNumber = "EX-12345"
            // Material, PreviousAccreditationYear, ComplianceIssuesReported all null
        };

        var recommendation = _service.EvaluateRecommendation(payload);

        Assert.Equal(ReAccreditationRecommendation.MoreInfoNeeded, recommendation.Outcome);
    }

    [Fact]
    public void Recommends_more_info_needed_when_material_is_blank()
    {
        var payload = new ReAccreditationPayload
        {
            OrganisationName = "Acme Recycling Ltd",
            RegistrationNumber = "EX-12345",
            Material = "  ",
            PreviousAccreditationYear = 2024,
            ComplianceIssuesReported = 0
        };

        var recommendation = _service.EvaluateRecommendation(payload);

        Assert.Equal(ReAccreditationRecommendation.MoreInfoNeeded, recommendation.Outcome);
    }

    [Fact]
    public void Throws_when_payload_is_null()
    {
        Assert.Throws<ArgumentNullException>(() => _service.EvaluateRecommendation(null!));
    }
}