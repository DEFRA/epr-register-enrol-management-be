using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;

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

    // ── RA-295: SLA clock ────────────────────────────────────────────────────

    [Fact]
    public void Build_items_past_submitted_carry_an_sla_clock()
    {
        // Every state after `submitted` is only reachable via `duly-made`,
        // which stamps the clock. Without one the case header and the
        // Applications card can render no "Due on" date.
        var items = BuildSeeder().Build(new ReAccreditationType(), BuildTime()).ToList();
        var past = items.Where(i => i.StateId != "submitted").ToList();

        Assert.NotEmpty(past);
        Assert.All(past, item =>
        {
            Assert.NotNull(item.SlaClock);
            Assert.Equal(item.SubmittedAt.AddDays(1), item.SlaClock!.StartedAt);
            Assert.False(item.SlaClock.Breached);
        });
    }

    [Fact]
    public void Build_submitted_items_have_no_sla_clock()
    {
        // The clock has genuinely not started yet, so the due date must stay
        // null and the UI must render a dash rather than a fabricated date.
        var items = BuildSeeder().Build(new ReAccreditationType(), BuildTime()).ToList();
        var submitted = items.Where(i => i.StateId == "submitted").ToList();

        Assert.NotEmpty(submitted);
        Assert.All(submitted, item => Assert.Null(item.SlaClock));
    }

    [Fact]
    public void Build_full_payload_fixture_lists_more_than_one_sampling_plan_document()
    {
        // AC02: supporting documents "should be listed" (plural). A
        // single-file fixture cannot distinguish a template that renders
        // every file from one that renders files[0] and stops.
        var items = BuildSeeder().Build(new ReAccreditationType(), BuildTime()).ToList();
        var fullPayload = items.Single(i =>
            i.Payload.Contains("organisationName") &&
            i.Payload["organisationName"].AsString == "Full Payload Verification Ltd");

        var files = fullPayload.Payload["samplingPlan"]["files"].AsBsonArray;

        Assert.True(files.Count > 1);
        Assert.Equal(
            files.Select(f => f["filename"].AsString).Distinct().Count(),
            files.Count);
    }

    // ── RA-292: ORS / interim site / authority-to-issue fixture ──────────────

    /// <summary>
    /// The RA-292 fixture, located the way mgmt-tests locates it — by the
    /// organisation name the work-items list is searchable on.
    /// </summary>
    private static WorkItem BuildOrsFixture()
    {
        var items = BuildSeeder().Build(new ReAccreditationType(), BuildTime()).ToList();
        return items.Single(i =>
            i.Payload.Contains("organisationName") &&
            i.Payload["organisationName"].AsString ==
                ReAccreditationSeeder.OrsInterimAuthorityOrganisationName);
    }

    private static BsonArray OrsSites(WorkItem item) =>
        item.Payload["overseasSites"]["sites"].AsBsonArray;

    [Fact]
    public void Build_ra292_fixture_organisation_name_is_unique_across_the_seed_set()
    {
        // mgmt-tests reaches this item by searching the work-items list on the
        // organisation name and asserting exactly one row. A duplicate here
        // would make that search ambiguous and the spec flaky.
        var items = BuildSeeder().Build(new ReAccreditationType(), BuildTime()).ToList();

        var matches = items.Count(i =>
            i.Payload.Contains("organisationName") &&
            i.Payload["organisationName"].AsString ==
                ReAccreditationSeeder.OrsInterimAuthorityOrganisationName);

        Assert.Equal(1, matches);
    }

    [Fact]
    public void Build_ra292_fixture_has_its_own_seed_key_so_it_lands_in_already_seeded_databases()
    {
        // Seeding is CreateIfAbsentAsync keyed by a deterministic id: it
        // inserts, it never updates. Enriching an existing seed item would
        // therefore be invisible in every environment that has already seeded.
        // Pin the fixture to its own key so the id differs from the one the
        // pre-RA-292 seed set already wrote.
        var expectedId = WorkItemSeed.DeterministicId(
            ReAccreditationType.Id, ReAccreditationSeeder.OrsInterimAuthoritySeedKey);

        Assert.Equal(expectedId, BuildOrsFixture().Id);
        Assert.NotEqual(
            WorkItemSeed.DeterministicId(ReAccreditationType.Id, "full-payload-verification"),
            expectedId);
    }

    [Fact]
    public void Build_ra292_fixture_carries_new_and_not_new_overseas_sites_on_one_item()
    {
        // AC01. Both polarities must sit on the SAME work item: a fixture that
        // only carries `isNewSite: true` cannot tell a correct implementation
        // from one that badges every site.
        var sites = OrsSites(BuildOrsFixture());

        Assert.Contains(sites, s =>
            s.AsBsonDocument.Contains("isNewSite") && s["isNewSite"].AsBoolean);
        Assert.Contains(sites, s =>
            s.AsBsonDocument.Contains("isNewSite") && !s["isNewSite"].AsBoolean);
    }

    [Fact]
    public void Build_ra292_fixture_carries_an_overseas_site_with_no_isNewSite_key()
    {
        // Every RA-292 field is optional on the wire, so "absent" is a real
        // rendering branch — and it is the one a pre-RA-292 submission takes.
        var sites = OrsSites(BuildOrsFixture());

        Assert.Contains(sites, s => !s.AsBsonDocument.Contains("isNewSite"));
    }

    [Fact]
    public void Build_ra292_fixture_carries_new_and_not_new_interim_sites()
    {
        // AC02. The interim site is nested inside a site, which is exactly the
        // shape a shallow payload merge or a typed model would drop.
        var sites = OrsSites(BuildOrsFixture());
        var interimFlags = sites
            .Select(s => s.AsBsonDocument)
            .Where(s => s.Contains("interimSite"))
            .Select(s => s["interimSite"]["isNewSite"].AsBoolean)
            .ToList();

        Assert.Contains(true, interimFlags);
        Assert.Contains(false, interimFlags);
    }

    [Fact]
    public void Build_ra292_fixture_carries_an_overseas_site_with_no_interim_site()
    {
        // A site need not have an interim site at all — the frontend must not
        // assume the key exists.
        var sites = OrsSites(BuildOrsFixture());

        Assert.Contains(sites, s => !s.AsBsonDocument.Contains("interimSite"));
    }

    [Fact]
    public void Build_ra292_fixture_populates_the_full_ors_detail_field_set()
    {
        // AC04: "the specific site data details are clearly displayed" needs a
        // fixture that actually has those details. Pins the wire contract
        // produced by the operator backend, so a field silently dropped from
        // the seed (and therefore never rendered or asserted) fails here.
        var site = OrsSites(BuildOrsFixture())
            .Select(s => s.AsBsonDocument)
            .Single(s => s.Contains("isNewSite") && s["isNewSite"].AsBoolean);

        string[] expected =
        [
            "siteId", "orsId", "siteName", "siteAddress", "addressLine1", "addressLine2",
            "townOrCity", "country", "coordinates", "contactName", "contactEmail",
            "contactPhone", "operationCode", "code1", "code2", "code3", "repatriatedLoads",
            "conditionsOfExport", "isEu", "isOecd", "isNewSite", "registeredNowAccredited",
            "besEvidence", "interimSite"
        ];

        var missing = expected.Where(f => !site.Contains(f)).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void Build_ra292_fixture_populates_the_full_interim_site_field_set()
    {
        // AC04, interim half of the contract.
        var interim = OrsSites(BuildOrsFixture())
            .Select(s => s.AsBsonDocument)
            .Where(s => s.Contains("interimSite"))
            .Select(s => s["interimSite"].AsBsonDocument)
            .Single(i => i["isNewSite"].AsBoolean);

        string[] expected =
        [
            "siteId", "siteNumber", "isNewSite", "country", "siteName", "addressLine1",
            "addressLine2", "townOrCity", "stateOrRegion", "postcode", "contactName",
            "contactEmail", "contactPhone"
        ];

        var missing = expected.Where(f => !interim.Contains(f)).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void Build_ra292_fixture_field_types_match_the_operator_backend_wire_contract()
    {
        // Types are pinned against a captured operator-backend payload, not its
        // model definitions, because two of them are counter-intuitive:
        // `repatriatedLoads` is a STRING and `conditionsOfExport` a nullable
        // BOOLEAN, while everything that reads like a flag really is boolean.
        // Seeding the wrong primitive renders plausibly in some templates and
        // silently breaks the badge logic in others, so the mismatch has to fail
        // here rather than in a browser.
        var sites = OrsSites(BuildOrsFixture()).Select(s => s.AsBsonDocument).ToList();

        foreach (var site in sites.Where(s => s.Contains("isNewSite")))
        {
            Assert.Equal(BsonType.Boolean, site["isNewSite"].BsonType);
        }

        foreach (var site in sites.Where(s => s.Contains("repatriatedLoads")))
        {
            Assert.Equal(BsonType.String, site["repatriatedLoads"].BsonType);
        }

        foreach (var site in sites.Where(s => s.Contains("conditionsOfExport")))
        {
            Assert.Equal(BsonType.Boolean, site["conditionsOfExport"].BsonType);
        }

        foreach (var site in sites.Where(s => s.Contains("coordinates")))
        {
            Assert.Equal(BsonType.String, site["coordinates"].BsonType);
        }

        foreach (var flag in new[] { "isEu", "isOecd", "registeredNowAccredited" })
        {
            foreach (var site in sites.Where(s => s.Contains(flag)))
            {
                Assert.Equal(BsonType.Boolean, site[flag].BsonType);
            }
        }

        foreach (var interim in sites
            .Where(s => s.Contains("interimSite"))
            .Select(s => s["interimSite"].AsBsonDocument))
        {
            Assert.Equal(BsonType.Boolean, interim["isNewSite"].BsonType);
        }

        foreach (var authoriser in BuildOrsFixture().Payload["prns"]["authorisers"].AsBsonArray
            .Select(a => a.AsBsonDocument)
            .Where(a => a.Contains("isNew")))
        {
            Assert.Equal(BsonType.Boolean, authoriser["isNew"].BsonType);
        }
    }

    [Fact]
    public void Build_ra292_fixture_carries_new_not_new_and_unflagged_authorisers()
    {
        // AC03, all three observable states of the authority-to-issue flag.
        var authorisers = BuildOrsFixture().Payload["prns"]["authorisers"].AsBsonArray
            .Select(a => a.AsBsonDocument)
            .ToList();

        Assert.Contains(authorisers, a => a.Contains("isNew") && a["isNew"].AsBoolean);
        Assert.Contains(authorisers, a => a.Contains("isNew") && !a["isNew"].AsBoolean);
        Assert.Contains(authorisers, a => !a.Contains("isNew"));
        Assert.All(authorisers, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a["fullName"].AsString));
            Assert.False(string.IsNullOrWhiteSpace(a["email"].AsString));
        });
    }

    [Fact]
    public void Build_ra292_fixture_exercises_both_polarities_of_isEu_and_isOecd()
    {
        // If the frontend renders these two field-by-field rather than through
        // a shared boolean helper, a fixture where every site is EU and OECD
        // lets the false case be dropped unnoticed. Reached with a genuinely
        // non-EU, non-OECD country rather than by mislabelling a European site
        // — a fixture that asserts something factually false to hit a code path
        // is worse than the gap it closes.
        var sites = OrsSites(BuildOrsFixture()).Select(s => s.AsBsonDocument).ToList();

        foreach (var flag in new[] { "isEu", "isOecd" })
        {
            Assert.Contains(sites, s => s.Contains(flag) && s[flag].AsBoolean);
            Assert.Contains(sites, s => s.Contains(flag) && !s[flag].AsBoolean);
        }
    }

    [Fact]
    public void Build_ra292_fixture_carries_an_empty_and_a_populated_bes_evidence_file_list()
    {
        // The producer always emits besEvidence with a files array, sending an
        // EMPTY array when there is no evidence rather than omitting the key.
        // That is a third rendering branch, distinct from a populated list and
        // from the key being absent altogether.
        var sites = OrsSites(BuildOrsFixture()).Select(s => s.AsBsonDocument).ToList();
        var fileLists = sites
            .Where(s => s.Contains("besEvidence"))
            .Select(s => s["besEvidence"]["files"].AsBsonArray)
            .ToList();

        Assert.Contains(fileLists, f => f.Count > 0);
        Assert.Contains(fileLists, f => f.Count == 0);
    }

    [Fact]
    public void Build_ra292_fixture_omits_optional_fields_rather_than_nulling_them()
    {
        // The producer serialises with WhenWritingNull, so an optional field
        // with no value is an ABSENT KEY. A null-valued key would misrepresent
        // a real submission and would exercise a rendering branch that cannot
        // occur in production.
        var sites = OrsSites(BuildOrsFixture()).Select(s => s.AsBsonDocument).ToList();

        Assert.All(sites, site =>
        {
            Assert.DoesNotContain(site.Elements, e => e.Value.IsBsonNull);
            if (site.Contains("interimSite"))
            {
                Assert.DoesNotContain(
                    site["interimSite"].AsBsonDocument.Elements, e => e.Value.IsBsonNull);
            }
        });

        Assert.All(
            BuildOrsFixture().Payload["prns"]["authorisers"].AsBsonArray
                .Select(a => a.AsBsonDocument),
            a => Assert.DoesNotContain(a.Elements, e => e.Value.IsBsonNull));
    }

    [Fact]
    public void Build_ra292_fixture_bes_evidence_file_keys_match_the_producer_casing()
    {
        // `filename` is all-lowercase at the producer. A `fileName` seed would
        // render blank in a template keyed on the real name, and no type in
        // this codebase would catch it.
        var file = OrsSites(BuildOrsFixture())
            .Select(s => s.AsBsonDocument)
            .Where(s => s.Contains("besEvidence"))
            .SelectMany(s => s["besEvidence"]["files"].AsBsonArray)
            .Select(f => f.AsBsonDocument)
            .First();

        Assert.True(file.Contains("filename"));
        Assert.False(file.Contains("fileName"));
    }

    [Fact]
    public void Build_ra292_fixture_bes_evidence_file_ids_are_unique_within_the_item()
    {
        // management-fe's download controller resolves a file by fileId across
        // the whole payload, so a collision would serve the wrong document.
        // The s3Key is deliberately shared with the full-payload fixture (one
        // localstack object, two work items) — only the ids must differ.
        var fileIds = OrsSites(BuildOrsFixture())
            .Select(s => s.AsBsonDocument)
            .Where(s => s.Contains("besEvidence"))
            .SelectMany(s => s["besEvidence"]["files"].AsBsonArray)
            .Select(f => f["fileId"].AsString)
            .ToList();

        Assert.NotEmpty(fileIds);
        Assert.Equal(fileIds.Count, fileIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Build_retains_a_work_item_with_no_overseas_sites_and_no_prns()
    {
        // The whole-item backwards-compatibility case: a pre-RA-292 submission
        // carries none of these keys, and the overview page must still render.
        // mgmt-tests already uses "Belfast Fibres Co" as its no-overseas-sites
        // fixture; this stops a future seed change from quietly enriching every
        // item and making that spec vacuous.
        var items = BuildSeeder().Build(new ReAccreditationType(), BuildTime()).ToList();

        Assert.Contains(items, i =>
            !i.Payload.Contains("overseasSites") && !i.Payload.Contains("prns"));
    }

    /// <summary>
    /// RA-316 regression guard. Seeded items are what every non-production
    /// environment renders, the journey-test stack included, and the
    /// duly-making page shows chargeAmountPence as the charge the regulator
    /// confirms before recording payment. A seed without it renders "Not
    /// provided" on the one screen whose entire purpose is confirming a
    /// payment — and because the payload model is [BsonIgnoreExtraElements]
    /// nothing throws, so the only signal is an e2e failure against a backend
    /// that is working perfectly. Assert it here, where the cause is obvious.
    /// </summary>
    [Fact]
    public void Every_seeded_item_carries_a_positive_charge_amount_in_pence()
    {
        var items = BuildSeeder().Build(new ReAccreditationType(), BuildTime()).ToList();

        Assert.NotEmpty(items);

        foreach (var item in items)
        {
            Assert.True(
                item.Payload.Contains("chargeAmountPence"),
                $"Seed '{item.Payload.GetValue("applicationReference", "?")}' has no "
                    + "chargeAmountPence; the duly-making page would render no charge."
            );
            var charge = item.Payload["chargeAmountPence"];
            Assert.True(charge.IsInt32 || charge.IsInt64, "chargeAmountPence must be an integer.");
            // Pence, not pounds. A three-figure value where four are expected is
            // the tell that someone stored 3276 instead of 327600.
            Assert.True(charge.ToInt64() > 0, "chargeAmountPence must be positive.");

            // RA-316: keep every seeded charge above £500 in pence. This looks
            // arbitrary and is not — it protects an assertion in another repo.
            //
            // The mgmt-tests journey suite catches a missing /100 in the
            // frontend by bounding the RENDERED charge below £50,000: correct
            // rendering of our smallest seed is £546.00, the same value rendered
            // undivided is £54,600.00, and the ceiling sits in that gap. That
            // check only discriminates while every seeded value, rendered
            // undivided, lands ABOVE the ceiling. Seed anything under 50000
            // pence — 32800 for a single overseas site's £328 increment is the
            // plausible mistake — and it renders undivided as £32,800, under the
            // ceiling, so a pounds/pence bug passes silently for that item.
            //
            // The coupling is real but invisible from the other repo, so it is
            // enforced here, where the values live. If a genuine sub-£500 fee
            // band ever exists, coordinate with mgmt-tests before seeding it
            // rather than deleting this assertion.
            Assert.True(
                charge.ToInt64() >= 50_000,
                $"Seeded chargeAmountPence {charge.ToInt64()} is below 50000 pence (£500). "
                    + "This silently weakens the mgmt-tests pounds/pence magnitude check — "
                    + "see the comment above before changing it."
            );
        }
    }

    /// <summary>
    /// paymentReference is an OVERRIDE, absent on real submissions because the
    /// operator backend has no payment reference at submission time — the
    /// frontend falls back to the application reference. Exactly one seed sets
    /// it, so both the override and the far more common fallback path are
    /// exercised in a seeded environment. Seeding it everywhere would leave the
    /// fallback untested.
    /// </summary>
    [Fact]
    public void Exactly_one_seeded_item_overrides_the_payment_reference()
    {
        var items = BuildSeeder().Build(new ReAccreditationType(), BuildTime()).ToList();

        var withOverride = items.Where(i => i.Payload.Contains("paymentReference")).ToList();

        Assert.Single(withOverride);
        Assert.False(
            string.IsNullOrWhiteSpace(withOverride[0].Payload["paymentReference"].AsString)
        );
        // Every other seed relies on the applicationReference fallback, which
        // the framework stamps on all of them.
        Assert.All(items, i => Assert.True(i.Payload.Contains("applicationReference")));
    }
}

