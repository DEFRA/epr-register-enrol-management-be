# Runbook: the `isNewSite` default defect (epr-2uxy)

Diagnostic and gated remediation for overseas sites whose frozen
`payload.overseasSites.sites[].isNewSite` may be a wrongly-defaulted `true`.

## TL;DR

**On CDP** (no ad-hoc `mongosh` against a deployed database):

```bash
Diagnostics__Ra292IsNewSiteAudit=true   # boot, read the log, turn it back off
```

**Locally, or anywhere with direct DB access:**

```bash
mongosh "$MONGO_URI" docs/diagnostics/ra292-isnewsite-audit.js
```

Both are read-only, classify identically, and print a count plus the
identifying work-item ids. The script additionally replaces every mutating
method on the collection handle with a throw, so it cannot write even if edited.

## What the defect is

Operator-side `OverseasSiteModel` gained `IsNewSite` with a `= true` property
initializer (`423d27d`, 2026-07-26). Applications are POCO-mapped into Mongo,
and **a missing BSON element leaves the C# property initializer rather than
`default(bool)`** — so any overseas site persisted before that date reads back
as `true` whatever it actually was.

From `9e8e9da` (2026-07-26, RA-294) that value began being transmitted to this
service, which **freezes** it onto the work item and never re-derives it.

RA-292 did not create this. It made it regulator-visible by rendering a "New"
badge from the field.

It cannot be fixed from the operator side alone: operator-side `UpdateAsync`
uses `ReplaceOneAsync`, so the first save after 2026-07-26 persists the wrong
`true` permanently; and the copy frozen here was taken at submission. Correcting
the model default stops the bleeding but repairs neither.

## The discriminator: `orsId`

Established from operator-backend git history, not inferred:

- **ReEx-sourced sites have never carried an `orsId`.** `HttpReExApiAdapter` has
  never set it in any revision (`git log -S OrsId` over that file returns zero
  commits); nor does the stub adapter or `ApplyPromotedFields`, so even promoted
  ReEx sites have none. It sets `IsNewSite = false` explicitly.
- **Operator-added sites always carry one.** `AddOverseasSiteRequest.OrsId` is
  `required` and validated `NotEmpty().MaximumLength(10)`.
- The operator serialiser uses `WhenWritingNull`, so a null `orsId` is **omitted**
  rather than sent as `"orsId": null`.

Those are the only creation paths. So, within the window:

| | `isNewSite: true` |
| --- | --- |
| `orsId` **present** | operator-added ⟹ **correct** |
| `orsId` **absent** | ReEx-sourced ⟹ **provably wrong** |

`orsId` entered the payload in the *same commit* that started transmitting
`isNewSite`, so there is no sub-window carrying the bad flag without the signal
beside it.

### There is deliberately no "every site is true" heuristic

An earlier revision of this diagnostic narrowed on "every ORS on the item is
`true`". That has been **removed**, not demoted. It was only ever a proxy for
provenance, and `orsId` gives provenance directly and per site — so it added
nothing while carrying a real misuse risk: an application where every overseas
site genuinely *is* new is ordinary (a first-time exporter, or a prior year that
carried no overseas sites, since operator-side `Seed` only populates
`OverseasSites` when `priorYearData.IsExporter`).

The rule it would have violated still governs everything here:

> **Render faithfully; fix the data where it lives.** A wrong value rendered
> honestly is traceable. A second-guessed one is not.

## The ambiguity guard

`orsId` was itself client-clobberable during the window: `string?` on the
operator model, with `PatchOverseasSites` replacing the site list wholesale. If
a client ever stripped it, an operator-added site would masquerade as
ReEx-sourced.

So a site missing `orsId` is only classed **provably corrupt** when it *also*
carries none of the detail fields a ReEx-mapped site never has — contact
details, operation code, waste codes, address lines, coordinates, repatriated
loads, conditions of export, BES evidence, interim site. (`MapOverseasSite`
populates only `SiteId`, `SiteName`, `SiteAddress`, `Country`, `IsEu`, `IsOecd`,
`Selected`, `IsNewSite`.)

A site missing `orsId` that *does* carry such detail is reported **ambiguous**.
The migration **refuses** to touch it — in code, not by relying on a human
noticing.

`OrsId` is now server-derived in `OverseasSiteMerge` (`8c4021e`), so from the
RA-292 deploy onward the discriminator is a guarantee rather than a happy
accident. That does **not** retroactively fix frozen payloads, which is exactly
why the spot-check below is still required.

## Not affected, and deliberately not scanned

- **`interimSite.isNewSite`** — `InterimSiteModel` and its flag were added in the
  same commit, so no interim site predates the flag, and ReEx never creates
  interim sites (every one comes from `AddInterimSite`, which sets the flag
  explicitly).
- **`prns.authorisers[].isNew`** — introduced by RA-292 and never previously
  transmitted.

The entire remediation surface is ORS-level `isNewSite`.

## The window

The date bound is the **primary** filter.

| Bound | Value | Why |
| --- | --- | --- |
| Start | 2026-07-26 (`9e8e9da`) | First build that transmitted `isNewSite` at all. Earlier items carry none, render no badge, and are not at risk. |
| End | RA-292 deploy | The window **closes** once the operator model default is corrected. The candidate set is finite and cannot grow. |

Supply the deploy time for a precise upper bound; it defaults to *now*, which
over-reports rather than under-reports:

```bash
# in-app
Diagnostics__Ra292DeployedAt=2026-08-11T09:00:00Z

# script
mongosh "$MONGO_URI" --eval "RA292_DEPLOYED_AT='2026-08-11T09:00:00Z'" \
  docs/diagnostics/ra292-isnewsite-audit.js
```

## Remediation

`ReAccreditationIsNewSiteCorrectionMigration` corrects **only** provably-corrupt
sites. It is registered in DI unconditionally but is a no-op until every gate is
satisfied.

### Why the gating is this heavy

The failure directions are not symmetric.

- The **defect** errs toward over-showing: a spurious "New" badge is visible,
  questionable and traceable.
- A **bad correction** errs toward under-showing: it stamps a fabricated `false`
  over a genuinely new site, hiding it from regulator scrutiny, silently.

The second is strictly worse. So the migration must not be able to run on
analysis alone — someone has to have looked at real records first.

### The four gates

| # | Gate | Effect |
| --- | --- | --- |
| 1 | `Diagnostics:Ra292CorrectIsNewSite` | Off by default — no-op in every environment until deliberately enabled |
| 2 | `Diagnostics:Ra292SpotCheckConfirmedBy` | Must name the human who spot-checked the classification against the operator DB. Recorded in the audit entry |
| 3 | `Diagnostics:Ra292CorrectIsNewSiteApply` | **Dry run unless explicitly true** — reports exactly what it would change and writes nothing |
| 4 | per-site verdict | Only `ProvablyCorrupt`. Ambiguous sites are refused in code |

Gate 2 is what makes *constructible* ≠ *safe to run unreviewed*. It is deliberately
a name rather than a boolean, so the authorisation is auditable rather than a
shell variable that vanishes.

### Running it

```bash
# 1. Count first (read-only).
Diagnostics__Ra292IsNewSiteAudit=true

# 2. If non-zero, spot-check a sample of the orsId-absent sites against the
#    operator database with the operator-backend owner. Confirm they really are
#    ReEx-sourced.

# 3. Dry run. Writes nothing; reports precisely what it would change.
Diagnostics__Ra292CorrectIsNewSite=true
Diagnostics__Ra292SpotCheckConfirmedBy="your.name"

# 4. Only when the dry-run output is what you expect:
Diagnostics__Ra292CorrectIsNewSiteApply=true

# 5. Turn all of it back off.
```

Idempotent — a corrected site reads `false` and classifies as `NotFlaggedNew` on
any later run. Every correction appends an `is-new-site-corrected` audit entry
naming the authorising spot-check, so the change is traceable on the work item
itself rather than only in deploy logs.

### Ambiguous sites

Never auto-corrected. Adjudicate by hand:

1. Take the ambiguous ids from the diagnostic.
2. Hand the per-site detail to the operator-backend owner to check against the
   operator database (`operatorApplicationId` / `operatorOrganisationId` are on
   the payload).
3. Correct only what comes back **provably** corrupt.
4. Record the residual on `epr-2uxy` so the decision is auditable rather than
   silently dropped.

### If the count is zero

Record the environments and figures on `epr-2uxy` and close it as prevented.
Leave the migration disabled; it costs nothing to carry.

### If ground truth had not been recoverable

Worth keeping on the record, since the outcome was close: had nothing
distinguished a defaulted `true` from a genuine one, the correct action would
have been to ship the diagnostic and a manual procedure and write **no
migration at all**. A migration that guessed would have been worse than the
defect. Do not treat "a migration exists" as evidence that guessing is
acceptable — it exists only because `orsId` makes the classification provable.
