using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Time.Testing;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// Pins the seeder's audit-trail contract. The audit log on the
/// <c>WorkItem</c> document is the authoritative record of who acted, so
/// any seeded data must satisfy the same invariants as a real
/// assignment performed through <c>WorkItemService.AssignAsync</c> —
/// most importantly, the assignee id is distinct from the id of the
/// user who made the assignment.
/// </summary>
public class ReAccreditationSeederTests
{
    [Fact]
    public void Build_attributes_assignment_to_seeder_sentinel_not_to_assignee()
    {
        // epr-ce4 regression guard: setting AssignedBy = AssignedToId
        // would falsify the audit trail to claim the assignee assigned
        // themselves.
        var seeder = new ReAccreditationSeeder();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));

        var items = seeder.Build(new ReAccreditationType(), time).ToList();

        Assert.NotEmpty(items);

        foreach (var item in items)
        {
            if (item.AssignedToId is null)
            {
                // Unassigned items have no AssignedBy either.
                Assert.Null(item.AssignedBy);
                continue;
            }

            Assert.Equal(ReAccreditationSeeder.SeederAssignedBy, item.AssignedBy);
            Assert.NotEqual(item.AssignedToId, item.AssignedBy);
        }
    }

    [Fact]
    public void Build_seeder_sentinel_is_namespaced_to_avoid_real_user_collision()
    {
        // The sentinel must not collide with any user id that could be
        // issued by Cognito / the BFF — those flow through as opaque
        // strings and a bare value like "seeder" or "system" could
        // theoretically be claimed. Namespace it with a colon so the
        // shape is obviously synthetic.
        Assert.Contains(":", ReAccreditationSeeder.SeederAssignedBy);
        Assert.StartsWith("system:", ReAccreditationSeeder.SeederAssignedBy);
    }
}
