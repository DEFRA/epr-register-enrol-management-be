using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.Workflows;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Workflows.StandardLifecycle;

/// <summary>RA-782 AC6: an ORS update takes effect from the UK date it was determined, not when contact is confirmed.</summary>
public sealed class StandardEffectiveDateTests
{
    private static WorkItemAuditEntry Determination(string outcome, DateTime at) =>
        new()
        {
            Action = StandardEffectiveDate.DeterminationMadeAction,
            ActionDisplayName = "Determination made",
            CreatedAt = at,
            Details = new() { ["outcome"] = outcome },
        };

    private static WorkItem WithAudit(params WorkItemAuditEntry[] entries) =>
        new()
        {
            TypeId = "update-overseas-site",
            StateId = "approved-pending-contact",
            AuditLog = [.. entries],
        };

    [Theory]
    // 23:30Z on 1 June is 00:30 on 2 June in the UK (BST).
    [InlineData("2026-06-01T23:30:00Z", "2026-06-02")]
    [InlineData("2026-06-01T22:59:00Z", "2026-06-01")]
    // In winter the UK is on UTC.
    [InlineData("2026-12-01T23:30:00Z", "2026-12-01")]
    public void An_approval_takes_effect_from_the_uk_date_it_was_determined(string determinedAt, string expected)
    {
        var workItem = WithAudit(
            Determination(StandardWorkflow.ApprovedStateId, DateTime.Parse(determinedAt).ToUniversalTime())
        );

        Assert.Equal(DateOnly.Parse(expected), StandardEffectiveDate.OfApprovedDetermination(workItem));
    }

    [Fact]
    public void A_refused_determination_gives_no_approval_date()
    {
        var workItem = WithAudit(Determination(StandardWorkflow.RejectedStateId, new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc)));

        Assert.Null(StandardEffectiveDate.OfApprovedDetermination(workItem));
    }
}
