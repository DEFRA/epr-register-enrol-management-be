# RA-764: rename organisation 500069 on its 2027 cases

Organisation 500069 changed its name on the Re/Ex side of RREPW and the new
name must show from 2027. Each 2027 application keeps the copy of the name
taken when it was started, so `ReAccreditationOrganisationNameCorrectionMigration`
rewrites it on that organisation's 2027 re-accreditation work items:

| Org ID | Registration | Old name | New name |
| --- | --- | --- | --- |
| 500069 | R26NR5000690003GL | `Encirc Ltd` | `VIDRALA UK & IRELAND LIMITED` |

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

Candidates are found by their exact `payload.registrationNumber`
(`R26NR5000690003GL`); any with an `accreditationYear` other than 2027 are left
alone. Each 2027 candidate must also carry `operatorOrgNumber` 500069 (when the
field is present; older submissions predate it) and, once the 2027
accreditation is issued, an accreditation number like `A27NR500069####GL`. The
2026 accreditation number the ticket quotes (`A26NR5000690003GL`) is not held on
the 2027 case. Then:

- name `Encirc Ltd`: renamed with one atomic update filtered on the id **and**
  the old name;
- name `VIDRALA UK & IRELAND LIMITED`: already done, skipped (no write), so a
  re-run is a no-op;
- any other name, an identity mismatch, a missing year, or no 2027 case at all:
  the **whole run aborts before any write** and logs why.

Every 2027 case for the registration is renamed (for example a withdrawn
application and its resubmission).

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

`ReAccreditationTestOrganisationFixtures` seeds a 500069-identity case
(`00000764-0000-4000-8000-000000000764`, `RA-900000764`, old name, no audit
entries) wherever test organisations are enabled. The mgmt-tests compose stack
sets the three keys above in its `docker/config/management-be.env`, so the
fixture is renamed on every boot and the journey spec asserts the new name,
the search behaviour and the empty history. On CDP test the keys are not set
and the fixture keeps the old name.

## Removal

One-off: once applied in prod, remove the migration, its tests, this runbook
and the two `IWorkItemPersistence` methods only it calls
(`FindByPayloadFieldAsync`, `CompareAndSetPayloadFieldAsync`), as RA-691 did.
