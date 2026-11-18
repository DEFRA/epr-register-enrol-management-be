# Runbook: the `isNewSite` default defect (RA-794)

Diagnostic and gated remediation for overseas sites whose frozen
`payload.overseasSites.sites[].isNewSite` may be a wrongly-defaulted `true`.

## TL;DR

**On CDP** (no ad-hoc `mongosh` against a deployed database):

```bash
Diagnostics__Ra292IsNewSiteAudit=true   # boot, read the log, turn it back off
Diagnostics__Ra292DeployedAt=2026-08-11T09:00:00Z   # required: the RA-292 deploy time
```

**Locally, or anywhere with direct DB access:**

```bash
mongosh "$MONGO_URI" --eval "RA292_DEPLOYED_AT='2026-08-11T09:00:00Z'" \
  docs/diagnostics/ra292-isnewsite-audit.js
```

The deploy time is required by both; each refuses to run without it (see
[The window](#the-window)).

Both are read-only, classify identically, and print a count plus the
identifying work-item ids. The script additionally replaces every mutating
method on the collection handle with a throw, so it cannot write even if edited.

**To correct anything, the operator backend's correction runs first** — this
service's copy is overwritten from the operator database on the next query →
resubmit. See [Running it](#running-it).

## What the defect is

Operator-side `OverseasSiteModel` gained `IsNewSite` with a `= true` property
initializer (`423d27d`, 2026-07-26). Applications are POCO-mapped into Mongo,
and **a missing BSON element leaves the C# property initializer rather than
`default(bool)`** — so any overseas site persisted before that date reads back
as `true` whatever it actually was.

From `9e8e9da` (2026-07-26, RA-294) that value began being transmitted to this
service, which stores it on the work item at submission and never re-derives it
itself.

RA-292 did not create this. It made it regulator-visible by rendering a "New"
badge from the field.

Correcting the model default stops the bleeding but repairs nothing already
stored, and the wrong value lives in **two** places:

1. **The operator database.** Operator-side `UpdateAsync` uses
   `ReplaceOneAsync`, so the first save after 2026-07-26 persisted the wrong
   `true` permanently, and `OverseasSiteMerge` restores the stored `IsNewSite`
   on every later PATCH.
2. **The work-item payload here**, copied from (1) at submission.

### Correcting only the payload here is not durable

A query → resubmit **overwrites `payload.overseasSites` with the operator
database's copy**. In `ReAccreditationResumeService`:

- `s_canonicalPayloadFieldBySectionKey` maps both `["OverseasSites"]` and
  `["BesEvidence"]` to `"overseasSites"`
  (`WorkItems/ReAccreditation/ReAccreditationResumeService.cs:147-155`).
- `StampLatestSectionsAsync` writes each such resubmitted section with
  `persistence.SetPayloadFieldAsync` (`ReAccreditationResumeService.cs:485-494`),
  which is a whole-field `$set` of `payload.overseasSites`
  (`WorkItems/Core/WorkItemPersistence.cs:721-740`).
- The operator backend builds that section's `sites[]` from its own stored sites,
  `isNewSite = s.IsNewSite` (epr-register-enrol-backend
  `AccreditationApplication/Adapters/HttpCaseWorkingApiAdapter.cs`,
  `BuildOverseasSitesArray`), for both the overseas-sites and the
  broadly-equivalent-standards sections.

So if only this service's migration has run, the next resubmit of either section
puts the wrong `true`, and the "New" badge, back on the work item. **The
operator-side correction must run first** — see [Running it](#running-it).

## The discriminator: `orsId`

Established from operator-backend git history, not inferred:

- **ReEx-sourced sites carried no `orsId` until RA-507 (2026-08-27).** Before
  then `HttpReExApiAdapter` never set it; nor does the stub adapter or
  `ApplyPromotedFields`, so even promoted ReEx sites have none. It sets
  `IsNewSite = false` explicitly. RA-507 made `HttpReExApiAdapter.MapOverseasSite`
  populate `orsId` from ReEx's own ORS id, so "no `orsId`" is a ReEx signal only
  inside the window below, which closed before RA-507 shipped.
- **Operator-added sites always carry one.** `AddOverseasSiteRequest.OrsId` is
  `required` and validated `NotEmpty().MaximumLength(10)`.
- The operator serialiser uses `WhenWritingNull`, so a null `orsId` is **omitted**
  rather than sent as `"orsId": null`.

Those are the creation paths through the dedicated endpoints. Until RA-794's
operator-backend change, a PATCH of the site list could also introduce a site
the server had not seen before, with whatever `orsId` the client sent (see
[The ambiguity guard](#the-ambiguity-guard-and-why-promotion-resolves-it)). So,
within the window:

| | `isNewSite: true` |
| --- | --- |
| `orsId` **present** | operator-added ⟹ **correct** |
| `orsId` **absent** | ReEx-sourced ⟹ **provably wrong** |

`orsId` entered the payload in the *same commit* that started transmitting
`isNewSite`, so there is no sub-window carrying the bad flag without the signal
beside it.

## The four buckets

These names are used identically in the log output, the mongosh script, this
runbook and `RA-794`.

| Bucket | Shape | Action |
| --- | --- | --- |
| **PROVABLY-CORRUPT** | no `orsId`, unpromoted, no unexplained detail | correctable |
| **PROMOTED-CORRECTABLE** | no `orsId`, `registeredNowAccredited: true` | correctable |
| **AMBIGUOUS-REFUSED** | no `orsId`, unpromoted, unexplained detail present | refused; adjudicate by hand |
| **ALREADY-CORRECT** | `orsId` present | leave alone |

Counts are reported **per bucket**, never as a single "at risk" total — the
buckets fail in different directions, and collapsing them hides the one failure
mode this classifier actually has.

### A large refused bucket means question the classifier

> If **AMBIGUOUS-REFUSED exceeds TOTAL CORRECTABLE**, treat that as a reason to
> **question the classifier**, not as evidence the data cannot be remediated.

This classifier's failure mode is silent and in the *safe* direction: a
miscalibrated tell refuses records rather than corrupting them. So it surfaces
only as a large refused bucket that reads as appropriate caution — and the
natural conclusion, *"the data is too messy to remediate"*, would close
`RA-794` as intractable when it is not. Both the in-app diagnostic and the
script warn explicitly when the refused bucket is the larger one.

**Worked example — this actually happened during this change.** The first
version of this classifier counted `besEvidence` and `interimSite` as evidence
of operator creation and had no promotion resolver. Against a promoted ReEx site
it tripped on **thirteen fields at once** and refused it. Since promoted sites
are the bulk of the affected population, the migration would have been close to
a no-op while reporting a large, entirely plausible-looking ambiguous bucket.
It was caught by a teammate reading the operator endpoints, not by the report.

The diagnostic against that recurrence: every refused record names the fields
that caused the refusal, in a `refusedBecause` list. **If the same fields recur
across many records, the tell is over-broad rather than the data messy.**

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

## The ambiguity guard, and why promotion resolves it

`orsId` was itself client-clobberable during the window: `string?` on the
operator model, with `PatchOverseasSites` replacing the site list wholesale. If
a client ever stripped it, an operator-added site would masquerade as
ReEx-sourced. Hence a guard.

But **"carries operator detail" is the wrong tell on its own.**
`PromoteOverseasSite` has no ReEx-provenance guard, and `ApplyPromotedFields`
writes eighteen fields onto the site while never setting `OrsId`. So a promoted
registered site has **no `orsId` and the complete operator-detail set** — and
promoted sites *are* ReEx-sourced legacy sites, i.e. exactly the population this
exists to fix.

`registeredNowAccredited` (set only by promotion) is therefore used as a
**positive resolver**: it *explains* the detail rather than contradicting it.

Two field groups are deliberately **not** in the tell, both verified against the
operator endpoints:

- `siteName`, `siteAddress`, `country`, `isEu`, `isOecd`, `selected` —
  `MapOverseasSite` sets all of these, so they are on every ReEx site.
- **`besEvidence` and `interimSite`** — `AddBesEvidenceFile` and `AddInterimSite`
  both resolve the target by `SiteId` alone with **no provenance guard**, so an
  operator can and routinely does attach either to a carried-over ReEx site;
  uploading broadly-equivalent-standards evidence against a prior-year overseas
  site is the *purpose* of that journey. They evidence operator **activity on** a
  site, not operator **creation of** one. "ReEx never produces either" is true
  and is the wrong test.

### The safety asymmetry

An operator-**added** site always carries an `orsId`, so it never enters the
no-`orsId` population at all. `registeredNowAccredited` only ever disambiguates
*within* a set that is already ReEx-sourced by construction.

**Residual, accepted:** an operator-added site that was *both* promoted *and* had
its `orsId` stripped would be corrected wrongly. That requires promoting a site
that was never registered — contrived, and it is precisely what the human
spot-check gate exists to catch.

Since `8c4021e` (RA-292), `OverseasSiteMerge` restores the stored `orsId` for a
site the server already holds, so a PATCH can no longer strip it from an existing
site. It did **not** make the discriminator a guarantee: until RA-794, a site
the server had never seen was stamped `isNewSite = true` and kept whatever
`orsId` the client sent, which could be none (epr-register-enrol-backend
`AccreditationApplication/Services/OverseasSiteMerge.cs`). RA-794's
operator-backend change rejects such a PATCH with a 400, but sites created that
way before it shipped remain. That is why the spot-check below is still
required, and why the window keeps its upper bound.

## Not affected, and deliberately not scanned

- **`interimSite.isNewSite`** — `InterimSiteModel` and its flag were added in the
  same commit, so no interim site predates the flag, and ReEx never creates
  interim sites (every one comes from `AddInterimSite`, which sets the flag
  explicitly).
- **`prns.authorisers[].isNew`** — introduced by RA-292 and never previously
  transmitted.

The entire remediation surface is ORS-level `isNewSite`.

## The window

The date bound is the **primary** filter. It is applied to the work item's
`submittedAt`.

| Bound | Value | Why |
| --- | --- | --- |
| Start | 2026-07-26 (`9e8e9da`) | First build that transmitted `isNewSite` at all. Earlier items carry none, render no badge, and are not at risk. |
| End | RA-292 deploy | The operator model default was corrected then, so no **new** wrong value is created after it. |

**The deploy time is required.** The in-app audit and the correction both
refuse to run, logging at Error, when `Diagnostics:Ra292DeployedAt` is absent or
not a date-time; the script throws. There is no safe default, in either
direction:

- **Under-correction.** After RA-507 a ReEx-sourced site carries an `orsId`, so
  a post-RA-507 item would be classified ALREADY-CORRECT purely because it has
  one.
- **Wrong correction.** After the RA-292 deploy, a no-`orsId` site with
  `isNewSite: true` is no longer evidence of the defect: until RA-794's
  operator-backend change, a site added by PATCH was stamped `true` and could
  carry no `orsId` (see
  [The ambiguity guard](#the-ambiguity-guard-and-why-promotion-resolves-it)). An
  unbounded window could stamp `false` over a genuinely new site.

The window does **not** make the affected set finite while the operator database
is uncorrected. A work item **submitted after** the RA-292 deploy, from an
application whose site was saved with the wrong `true` during the window, still
carries that `true` — and falls outside this window, so neither the diagnostic
nor the migration here sees it. Running the operator-side correction stops new
ones; items already submitted between the RA-292 deploy and that correction are
a residual this service's migration does not reach (see
[What this does not reach](#what-this-does-not-reach)).

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

### The five gates

| # | Gate | Effect |
| --- | --- | --- |
| 1 | `Diagnostics:Ra292CorrectIsNewSite` | Off by default — no-op in every environment until deliberately enabled |
| 2 | `Diagnostics:Ra292SpotCheckConfirmedBy` | Must name the human who spot-checked the classification against the operator DB. Recorded in the audit entry |
| 3 | `Diagnostics:Ra292OperatorRefusedSites` | The sites the operator-side correction refused, or `none`. **No default** — absent, blank or malformed, nothing runs (dry run included), logging at Error. See [The operator-side refusals](#the-operator-side-refusals) |
| 4 | `Diagnostics:Ra292CorrectIsNewSiteApply` | **Dry run unless explicitly true** — reports exactly what it would change and writes nothing |
| 5 | per-site verdict | Only the two correctable buckets, and of those only sites the operator side did not refuse. AMBIGUOUS-REFUSED sites are refused **in code**, with the triggering fields named; so are operator-refused sites, and correctable sites that cannot be matched to the operator refusals |

Once gates 1 and 2 pass, the correction (like the audit, once enabled) refuses
to run, logging at Error, unless `Diagnostics:Ra292DeployedAt` is a valid
date-time — see [The window](#the-window). Gate 3 is checked next, then gate 4.

Gate 2 is what makes *constructible* ≠ *safe to run unreviewed*. It is deliberately
a name rather than a boolean, so the authorisation is auditable rather than a
shell variable that vanishes.

### The operator-side refusals

The operator backend's classifier is stricter than this one. It corrects a site
with no `orsId` only when ReEx itself returns a matching prior-year site, and
refuses the rest as AMBIGUOUS-REFUSED, because such a site may be genuinely new
(it may have come through the overseas-sites PATCH hole that
epr-register-enrol-backend closed in the same change). Here the same site can
read as PROVABLY-CORRUPT. Clearing it here would hide a possibly-new site from
the regulator, and the next resubmit would put the operator database's `true`
back anyway. So the operator side's refusals are carried across and refused
here **in code**.

`Diagnostics:Ra292OperatorRefusedSites` takes one of:

- `none` — the operator-side correction's final APPLY run refused no site: its
  completion line (`RA-794 isNewSite correction complete. Mode=APPLY. ...`)
  reads `Sites refused as ambiguous (adjudicate by hand): 0`. Absent or blank
  does **not** mean "none"; it means the list was not carried across, and the
  correction refuses to run.
- a comma-separated list of `<ApplicationId>:<SiteId>` pairs, one for each
  `RA-794 REFUSED to correct application {ApplicationId} site {SiteId} '...'`
  Warning line of that APPLY run, copied exactly. For example
  `65f0c0ffee0000000000abcd:3,65f0c0ffee0000000000abcd:7`. The number of pairs
  must equal that run's `Sites refused as ambiguous (adjudicate by hand)` count.
  The operator audit's `RA-794 AMBIGUOUS-REFUSED application ...` lines name the
  same sites and are a cross-check only; the APPLY run's REFUSED lines are what
  gets carried.

How the pairs match: `{ApplicationId}` is the operator application's 24-hex
ObjectId, which the operator backend sends as the work item's
`payload.operatorApplicationId` (`HttpCaseWorkingApiAdapter`,
`operatorApplicationId = application.ApplicationId`); `{SiteId}` is its integer
`OverseasSiteModel.SiteId`, sent as `payload.overseasSites.sites[].siteId`. The
application id matches case-insensitively and whitespace around entries is
ignored. Anything else in an entry (a missing part, a non-ObjectId application
id such as an application reference, a non-integer site id, a trailing comma,
`none` alongside pairs) stops the whole run at Error: dropping a mistyped entry
would leave the site it was meant to exclude unexcluded.

Per site, the correction then:

- refuses, at Warning, every correctable site whose pair is listed (never
  written, counted as `Sites refused because the operator side refused them`);
- refuses, at Error, every correctable site it cannot match because the work item
  has no `operatorApplicationId` or the site has no integer `siteId` (never
  written, counted as `Sites refused for want of an id to match`);
- names, at Warning, every listed pair that matched no site on any work item in
  the window. That can be legitimate (the application never reached a work item
  in the window) but it is also what a mistyped id looks like, so the
  spot-check confirmer accounts for each one before apply.

Every correction's audit entry records `operatorRefusedSitesListed`, the number
of pairs it was checked against.

### Running it

**Order: operator backend first, then this service.** A query → resubmit copies
the operator database's sites over `payload.overseasSites` (see
[Correcting only the payload here is not durable](#correcting-only-the-payload-here-is-not-durable)),
so a correction made here before the operator database is fixed is undone by the
next resubmit of the overseas-sites or broadly-equivalent-standards section.

1. **Operator backend** (epr-register-enrol-backend): run its correction to
   completion — audit, spot-check, dry run, apply, turn off — per its runbook,
   [`docs/diagnostics/ra-794-isnewsite-correction.md`](https://github.com/DEFRA/epr-register-enrol-backend/blob/main/docs/diagnostics/ra-794-isnewsite-correction.md).
   It uses the same config keys (`Diagnostics:Ra292IsNewSiteAudit`,
   `Diagnostics:Ra292DeployedAt`, `Diagnostics:Ra292CorrectIsNewSite`,
   `Diagnostics:Ra292SpotCheckConfirmedBy`,
   `Diagnostics:Ra292CorrectIsNewSiteApply`) and the same gate shape as below,
   set on the operator backend's own deployment. Its bucket names are the same,
   but its classifier is stricter: it corrects a site with no `orsId` only when
   ReEx itself returns a matching prior-year site, and refuses the rest as
   AMBIGUOUS-REFUSED. Its window bounds the application's creation time, not
   the work item's submission time.
   It has run to completion only when its final APPLY run logged no
   `RA-794: application {ApplicationId} was not corrected: it changed after
   planning or the write failed (...)` Error lines. Those applications still
   hold the wrong `true`, so a resubmit would undo a correction made here; if
   there are any, re-run the operator dry run and then apply, and only then
   continue. They are **not** carried in the refused list.
2. **Carry the operator refusals across.** From that final APPLY run, build
   `Diagnostics:Ra292OperatorRefusedSites` as described in
   [The operator-side refusals](#the-operator-side-refusals): one
   `<ApplicationId>:<SiteId>` pair per `RA-794 REFUSED to correct application
   ...` line, or `none` when its completion line shows 0 refused. Check the
   pair count against that completion line.
3. **Then this service:**

```bash
# 1. Count first (read-only). Always pin the window end (see "The window").
Diagnostics__Ra292IsNewSiteAudit=true
Diagnostics__Ra292DeployedAt=<RA-292 deploy time, ISO 8601 UTC>

# 2. If non-zero, spot-check a sample of the orsId-absent sites against the
#    operator database with the operator-backend owner. Confirm they really are
#    ReEx-sourced. Confirm too that the refused list below is complete: one pair
#    per REFUSED line of the operator's final APPLY run, the count matching its
#    completion line. Setting your name below attests to both.

# 3. Dry run. Writes nothing; reports precisely what it would change, and
#    names any listed pair that matched no site (account for each one).
Diagnostics__Ra292CorrectIsNewSite=true
Diagnostics__Ra292SpotCheckConfirmedBy="your.name"
Diagnostics__Ra292OperatorRefusedSites="<ApplicationId>:<SiteId>,..."   # or none

# 4. Only when the dry-run output is what you expect:
Diagnostics__Ra292CorrectIsNewSiteApply=true

# 5. Turn all of it back off.
```

Idempotent — a corrected site reads `false` and classifies as `NotFlaggedNew` on
any later run. Every correction appends an `is-new-site-corrected` audit entry
naming the authorising spot-check, so the change is traceable on the work item
itself rather than only in deploy logs.

### Ambiguous sites

Never auto-corrected: neither this side's AMBIGUOUS-REFUSED sites nor the
operator-refused and unmatchable sites the correction refuses. Adjudicate by
hand:

1. Take the ambiguous ids from the diagnostic, and the refused sites from the
   correction's REFUSED lines.
2. Hand the per-site detail to the operator-backend owner to check against the
   operator database (`operatorApplicationId` / `operatorOrganisationId` are on
   the payload).
3. Correct only what comes back **provably** corrupt.
4. Record the residual on `RA-794` so the decision is auditable rather than
   silently dropped.

### What this does not reach

Work items **submitted after** the RA-292 deploy whose sites still carried the
operator database's wrong `true` at submission. They fall outside the window
(see [The window](#the-window)), and widening the window is not safe after
RA-507. The operator-side correction fixes the source, so such an item is
corrected the next time its overseas-sites or broadly-equivalent-standards
section is resubmitted after a query — but not otherwise. Any remaining
population needs a human decision; record it on `RA-794` rather than
dropping it.

### If the count is zero

A zero here says nothing about the operator database. Close `RA-794` as
prevented only when the operator-side audit is also zero (or its correction has
been applied) and the population in
[What this does not reach](#what-this-does-not-reach) has been accounted for.
Record the environments and figures from both sides on `RA-794`. Leave both
corrections disabled; they cost nothing to carry.

### If ground truth had not been recoverable

Worth keeping on the record, since the outcome was close: had nothing
distinguished a defaulted `true` from a genuine one, the correct action would
have been to ship the diagnostic and a manual procedure and write **no
migration at all**. A migration that guessed would have been worse than the
defect. Do not treat "a migration exists" as evidence that guessing is
acceptable — it exists only because `orsId` makes the classification provable.
