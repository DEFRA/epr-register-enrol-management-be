# RA-764: rename organisation 500069 on its 2027 cases

Organisation 500069 changed its name on the Re/Ex side of RREPW and the new
name must show from 2027. Each 2027 application keeps the copy of the name
taken when it was started, so `ReAccreditationOrganisationNameCorrectionMigration`
rewrites it on that organisation's 2027 re-accreditation work items:

| Org ID | Regulator | Registration | 2026 accreditation | 2027 accreditation accepted | Old name | New name |
| --- | --- | --- | --- | --- | --- | --- |
| 500069 | NIEA | `R26NR5000690003GR` | `A26NR5000690003GR` | `A27NR500069####GR` | `Encirc Ltd` | `VIDRALA UK & IRELAND LIMITED` |
| 500069 | EA | `R26ER5002260055GR` | `A26ER5002260023GR` | `A27ER500226####GR` | `Encirc Ltd` | `VIDRALA UK & IRELAND LIMITED` |

Organisation 500069 (VIDRALA UK & IRELAND LIMITED, company NI030990) holds
**two** glass-remelt (`GR`) registrations, and the 2027 cases under both are
renamed. The EA registration's reference carries `500226`, not 500069; the
public register lists 500069 as the organisation for both.

> **History.** The ticket quoted one registration, `R26NR5000690003GL`. The
> material code is wrong (`GL`; the real code is `GR`, glass remelt) and the EA
> registration was missing. The 17 Nov prod dry run aborted in the operator
> backend on those facts, and as first written this migration would have found
> no case on prod. The table above is the confirmed set (public register plus
> the business).

The operator backend (epr-register-enrol-backend) has its own RA-764 migration
with the same config keys, for the operator's copy of the name.

## What changes, and what does not

- `payload.organisationName` is the only copy of the name in case management.
  Both search paths read it: the Applications page's "Organisation name or ID"
  box (case-insensitive substring regex) and the `$text` index behind the
  `orgName` filter, which Mongo updates with the document. So after the rename
  the case is found by its new name and no longer by its old one.
- **No audit entry is written** and existing entries (some quote the old name)
  are not rewritten. State, references, task data, assignment and
  `lastModifiedAt` are untouched. Only `version` (optimistic concurrency) is
  bumped, so a concurrent read-modify-replace cannot write the old name back.
- No management-fe change: it reads the name from the payload.

## Matching rule

Candidates are found by an exact `payload.registrationNumber` match on either
registration (`R26NR5000690003GR`, `R26ER5002260055GR`; one lookup each). Any
with an `accreditationYear` other than 2027 are left alone. Each 2027 candidate
must also:

- carry one of the two registration numbers (always true for the exact lookup;
  checked anyway so nothing else can ever be renamed);
- carry `operatorOrgNumber` 500069 when the field is present (older submissions
  predate it). This applies to the EA case too;
- once the 2027 accreditation is issued, carry an accreditation number derived
  from **its own** registration. Derivation: the leading `R` becomes `A`,
  characters 2-3 become the year (`27`), and the rest is copied, with the
  four-digit sequence tolerated (it is issued per accreditation; the EA
  registration's 2026 accreditation already has sequence `0023`, not `0055`).
  So an NIEA case accepts `A27NR500069####GR` and an EA case accepts
  `A27ER500226####GR`. The other registration's number, `GL`, a 2026 number,
  or a non-digit sequence is a mismatch. The operator backend applies the same
  rule (same pairing, prefix, material and sequence tolerance).

The 2026 accreditation numbers are not held on the 2027 cases, so they are not
checked here. Then:

- name `Encirc Ltd`: renamed with one atomic update filtered on the id **and**
  the old name;
- name `VIDRALA UK & IRELAND LIMITED`: already done, skipped (no write), so a
  re-run is a no-op;
- any other name, an identity mismatch, a missing year, or no 2027 case across
  both registrations: the **whole run aborts before any write** and logs why.

Every 2027 case under either registration is renamed in the same run (for
example the NIEA and EA cases, or a withdrawn application and its
resubmission). Expect the dry run to list at least one case per registration
that has a 2027 application.

## The three gates

| # | Config key (env var) | Effect |
| --- | --- | --- |
| 1 | `Migrations:Ra764OrganisationNameCorrection:Enabled` (`Migrations__Ra764OrganisationNameCorrection__Enabled`) | Off by default. On its own it is a **dry run**: logs `RA-764 DRY RUN would rename work item <id> (<ref>): Encirc Ltd → VIDRALA UK & IRELAND LIMITED` and writes nothing |
| 2 | `Migrations:Ra764OrganisationNameCorrection:Apply` (`Migrations__Ra764OrganisationNameCorrection__Apply`) | With 1, writes the renames, but only when 3 is also set |
| 3 | `Migrations:Ra764OrganisationNameCorrection:SpotCheckConfirmedBy` (`Migrations__Ra764OrganisationNameCorrection__SpotCheckConfirmedBy`) | Names whoever reviewed the dry-run output. Apply without it logs an error and stays a dry run |

## When it runs

At startup, in `IHostedLifecycleService.StartedAsync`: after every hosted
service has started, including the migration runner and the journey-test
fixture reset. That is why it is not an `IWorkItemMigration`: the runner starts
before the fixture reset, which would undo the rename of the RA-764 e2e fixture
on every boot. Production runs the same path; only the config gates it. A
failure (Mongo down, say) is logged as a warning and does not stop startup; the
next boot tries again.

## Running it

```bash
# 1. Dry run. Writes nothing; check the logged work item(s) against the live record.
Migrations__Ra764OrganisationNameCorrection__Enabled=true

# 2. Only when the dry run shows exactly the expected rename(s):
Migrations__Ra764OrganisationNameCorrection__Apply=true
Migrations__Ra764OrganisationNameCorrection__SpotCheckConfirmedBy=your.name

# 3. Turn all three back off. (Leaving them on is harmless: a re-run skips
#    the renamed case(s).)
```

An `RA-764 ABORTED, nothing written` error says what was found; it needs a
human decision, not a retry.

## E2E

`ReAccreditationTestOrganisationFixtures` seeds two 500069-identity cases
wherever test organisations are enabled, both on test organisation
`e2e-ra764-organisation-name`, unassigned, `submitted`, old name, no audit
entries and no accreditation number:

| Fixture id | Application reference | Registration | Nation |
| --- | --- | --- | --- |
| `00000764-0000-4000-8000-000000000764` | `RA-900000764` | `R26NR5000690003GR` | Northern Ireland |
| `00000764-0000-4000-8000-000000000765` | `RA-900000765` | `R26ER5002260055GR` | England |

The mgmt-tests compose stack sets the three keys above in its
`docker/config/management-be.env`, so both fixtures are renamed on every boot
and the journey spec asserts the new name on both, the search behaviour and the
empty history. On CDP test the keys are not set and the fixtures keep the old
name.

## Removal

One-off: once applied in prod, remove the migration, its tests, this runbook
and the two `IWorkItemPersistence` methods only it calls
(`FindByPayloadFieldAsync`, `CompareAndSetPayloadFieldAsync`), as RA-691 did.
