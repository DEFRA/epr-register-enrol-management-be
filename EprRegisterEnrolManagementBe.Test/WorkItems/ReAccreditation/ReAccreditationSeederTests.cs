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
///
/// RA-175: also pins the seed-data fixes — correct camelCase BSON keys,
/// audit log entries, applicant email, and nation derived via
/// <see cref="INationResolver"/>.
/// </summary>
public class ReAccreditationSeederTests
{
    private static ReAccreditationSeeder BuildSeeder() =>
        new(new NationResolver());

    private static FakeTimeProvider BuildTime() =>
        new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Build_attributes_assignment_to_seeder_sentinel_not_to_assignee()
    {
        // epr-ce4 regression guard: setting AssignedBy = AssignedToId
        // would falsify the audit trail to claim the assignee assigned
        // themselves.
        var items = BuildSeeder().Build(new ReAccreditationType(), BuildTime()).ToList();

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

    // RA-175 regression guards -----------------------------------------------

    [Fact]
    public void Build_every_item_has_work_item_submitted_audit_entry()
    {
        // WorkItemService.SubmitAsync writes this entry for real items; the
        // seeder must include it so the audit timeline starts at submission.
        var items = BuildSeeder().Build(new ReAccreditationType(), BuildTime()).ToList();

        Assert.All(items, item =>
            Assert.Contains(item.AuditLog, e => e.Action == "work-item-submitted"));
    }

    [Fact]
    public void Build_every_item_has_routed_to_nation_audit_entry()
    {
        // ReAccreditationNationRoutingHook writes this entry after a real
        // submission; the seeder must include it so the timeline is realistic.
        var items = BuildSeeder().Build(new ReAccreditationType(), BuildTime()).ToList();

        Assert.All(items, item =>
            Assert.Contains(item.AuditLog, e => e.Action == "routed-to-nation"));
    }

    [Fact]
    public void Build_routed_to_nation_entry_nation_matches_payload_nation()
    {
        // The audit entry and the payload must agree on the nation value.
        var items = BuildSeeder().Build(new ReAccreditationType(), BuildTime()).ToList();

        Assert.All(items, item =>
        {
            var entry = Assert.Single(item.AuditLog, e => e.Action == "routed-to-nation");
            var payloadNation = item.Payload.Contains("nation")
                ? item.Payload["nation"].AsString
                : null;
            Assert.Equal(payloadNation, entry.Details["nation"]);
        });
    }

    [Fact]
    public void Build_assigned_items_have_assigned_audit_entry()
    {
        // Mirrors WorkItemService.AssignAsync — assigned items need an
        // audit entry so the timeline shows who made the assignment.
        var items = BuildSeeder().Build(new ReAccreditationType(), BuildTime()).ToList();
        var assignedItems = items.Where(i => i.AssignedToId is not null).ToList();

        Assert.NotEmpty(assignedItems);
        Assert.All(assignedItems, item =>
            Assert.Contains(item.AuditLog, e => e.Action == "assigned"));
    }

    [Fact]
    public void Build_every_item_has_camelCase_nation_in_payload()
    {
        // The MongoDB query filters on "payload.nation" (camelCase). The old
        // seeder used "Nation" (PascalCase) which never matched the index.
        var items = BuildSeeder().Build(new ReAccreditationType(), BuildTime()).ToList();

        Assert.All(items, item =>
        {
            Assert.True(item.Payload.Contains("nation"),
                $"Item {item.Id} missing camelCase 'nation' key in payload.");
            Assert.False(item.Payload.Contains("Nation"),
                $"Item {item.Id} has PascalCase 'Nation' key — must be camelCase.");
        });
    }

    [Fact]
    public void Build_every_item_has_operator_email_in_payload()
    {
        // RA-175: operator email was absent from seeded items, breaking any
        // feature that reads or acts on the applicant email.
        var items = BuildSeeder().Build(new ReAccreditationType(), BuildTime()).ToList();

        Assert.All(items, item =>
        {
            Assert.True(item.Payload.Contains("operatorEmail"),
                $"Item {item.Id} missing 'operatorEmail' in payload.");
            var email = item.Payload["operatorEmail"].AsString;
            Assert.False(string.IsNullOrWhiteSpace(email),
                $"Item {item.Id} has blank operatorEmail.");
        });
    }

    [Fact]
    public void Build_every_item_has_operator_registration_id_in_payload()
    {
        // RA-223: the work-item detail page shows the operator's EPR
        // registration id from payload.operatorRegistrationId (the value the
        // legacy backend copies from application.RegistrationId). Without it
        // every seeded/demo item — and the e2e journey — would render "—".
        var items = BuildSeeder().Build(new ReAccreditationType(), BuildTime()).ToList();

        Assert.All(items, item =>
        {
            Assert.True(item.Payload.Contains("operatorRegistrationId"),
                $"Item {item.Id} missing 'operatorRegistrationId' in payload.");
            var registrationId = item.Payload["operatorRegistrationId"].AsString;
            Assert.False(string.IsNullOrWhiteSpace(registrationId),
                $"Item {item.Id} has blank operatorRegistrationId.");
        });
    }

    [Fact]
    public void Build_operator_registration_ids_are_distinct_per_item()
    {
        // RA-223: each seeded operator represents a distinct ReEx registration,
        // so the ids must not collide — a duplicate would misrepresent two
        // demo items as the same operator registration.
        var items = BuildSeeder().Build(new ReAccreditationType(), BuildTime()).ToList();

        var registrationIds = items
            .Select(i => i.Payload["operatorRegistrationId"].AsString)
            .ToList();

        Assert.Equal(registrationIds.Count, registrationIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Build_nation_is_derived_from_postcode_via_resolver()
    {
        // Spot-check a Scotland postcode (EH1 3BN) to confirm the seeder
        // calls INationResolver.Resolve rather than hard-coding strings.
        var items = BuildSeeder().Build(new ReAccreditationType(), BuildTime()).ToList();
        var scottishItem = items.Single(i =>
            i.Payload.Contains("siteAddressPostcode") &&
            i.Payload["siteAddressPostcode"].AsString.StartsWith("EH", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("Scotland", scottishItem.Payload["nation"].AsString);
    }

    [Fact]
    public void Build_audit_log_chronological_order()
    {
        // Audit entries must be in submission order so the timeline view
        // renders correctly without a sort.
        var items = BuildSeeder().Build(new ReAccreditationType(), BuildTime()).ToList();

        Assert.All(items, item =>
        {
            var timestamps = item.AuditLog.Select(e => e.CreatedAt).ToList();
            Assert.Equal(timestamps.OrderBy(t => t).ToList(), timestamps);
        });
    }
}

