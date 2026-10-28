# RA-691: correct two incorrectly sequenced accreditation numbers

Two approved 2027 renewals were issued accreditation numbers with a new
sequence segment instead of keeping the one from their 2026 number (root cause
RA-690, fixed separately). `ReAccreditationAccreditationNumberCorrectionMigration`
rewrites `payload.accreditationId` on exactly these two work items:

| Organisation | Org ID | Previous (2026) | Current (incorrect) | Corrected |
| --- | --- | --- | --- | --- |
| Infinity Plastic Solutions Ltd | 500527 | A26ER5005270246PL | A27ER5005270322PL | A27ER5005270246PL |
| PLASGRAN LIMITED | 500105 | A26ER5001050197PL | A27ER5001050321PL | A27ER5001050197PL |

Each correction appends an `accreditation-number-corrected` ("Accreditation
number corrected") audit entry with details `{ from, to, reason: "RA-691" }`,
shown on the Application history tab. There is no state change. Earlier audit
entries that quote the old number (`accreditation-issued`,
`determination-made`, `publishing-enqueued`) are history and are not
rewritten.

## Matching rule

The numeric organisation id exists in management-be only inside the number's
`{OrgId:D6}` segment (`payload.operatorOrganisationId` is the ReEx UUID), so
each target is located by its number's identity segments, e.g.
`^A27ER500527\d{4}PL$`, through an anchored-prefix query on the
`payload.accreditationId` index. Exactly one work item must match. Its current
value decides the outcome:

- incorrect number: corrected, with one atomic update filtered on the id and
  the incorrect value, which sets the field, appends the entry and bumps the
  version;
- corrected number: skipped (no write, no entry), so a re-run is a no-op;
- anything else, or zero or more than one candidate: the **whole run aborts
  before any write** and logs why.

## Configuration

The same shape as the backend's RA-691 correction, so both services behave alike.

| Config key (env var) | Default | Effect |
| --- | --- | --- |
| `Diagnostics:Ra691CorrectAccreditationNumbers` (`Diagnostics__Ra691CorrectAccreditationNumbers`) | `false` | Off: a no-op in every environment |
| `Diagnostics:Ra691CorrectAccreditationNumbersDryRun` (`Diagnostics__Ra691CorrectAccreditationNumbersDryRun`) | `true` | Dry run: logs `RA-691 DRY RUN would correct <org> (<org id>) work item <id>: old → new` per target and writes nothing. No spot-check name needed |
| `Diagnostics:Ra691SpotCheckConfirmedBy` (`Diagnostics__Ra691SpotCheckConfirmedBy`) | unset | Required only to apply: the name of whoever checked the corrected numbers against the 2026 numbers. With `DryRun=false` and no name, the run logs an error and runs as a dry run |

## Running it

```bash
# 1. Dry run. Writes nothing; logs "RA-691 DRY RUN would correct ..." per target.
Diagnostics__Ra691CorrectAccreditationNumbers=true

# 2. Only when the dry run shows exactly the two changes above, apply:
Diagnostics__Ra691CorrectAccreditationNumbersDryRun=false
Diagnostics__Ra691SpotCheckConfirmedBy="your.name"

# 3. Unset all three. (Leaving them set is harmless: a re-run skips both
#    items, but the keys serve no further purpose.)
```

An `RA-691 ABORTED, nothing written` error names the organisation and what was
found; it needs a human decision, not a retry.
