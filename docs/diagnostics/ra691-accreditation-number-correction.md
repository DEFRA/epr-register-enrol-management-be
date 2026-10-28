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

## The three gates

| # | Config key (env var) | Effect |
| --- | --- | --- |
| 1 | `Diagnostics:Ra691CorrectAccreditationNumbers` (`Diagnostics__Ra691CorrectAccreditationNumbers`) | Off by default; a no-op in every environment until set `true`. On its own it is a **dry run**: logs `org (id) work item <id>: old → new` per target and writes nothing |
| 2 | `Diagnostics:Ra691CorrectAccreditationNumbersApply` (`Diagnostics__Ra691CorrectAccreditationNumbersApply`) | With 1, writes the corrections — but only when 3 is also set |
| 3 | `Diagnostics:Ra691SpotCheckConfirmedBy` (`Diagnostics__Ra691SpotCheckConfirmedBy`) | Names whoever reviewed the dry-run output against the live records. Not needed for a dry run; Apply without it logs an error and stays a dry run. Logged with the applied run |

## Running it

```bash
# 1. Dry run. Writes nothing; logs "RA-691 DRY RUN would correct ..." per target.
Diagnostics__Ra691CorrectAccreditationNumbers=true

# 2. Only when the dry run shows exactly the two changes above:
Diagnostics__Ra691CorrectAccreditationNumbersApply=true
Diagnostics__Ra691SpotCheckConfirmedBy=your.name

# 3. Turn all three back off. (Leaving them on is harmless: a re-run skips
#    both items, but the keys serve no further purpose.)
```

An `RA-691 ABORTED, nothing written` error names the organisation and what was
found; it needs a human decision, not a retry.
