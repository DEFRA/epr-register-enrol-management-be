/**
 * DIAGNOSTIC (READ-ONLY): size and classify the epr-2uxy wrongly-defaulted
 * `isNewSite=true` population in the case-management store.
 *
 * This script COUNTS AND LISTS. It does not modify anything, and it is wired so
 * that it cannot — see the read-only guard below.
 *
 *   mongosh "mongodb://localhost:27017/epr-register-case-management" \
 *     docs/diagnostics/ra292-isnewsite-audit.js
 *
 * To bound the window at a known RA-292 deploy time rather than "now":
 *
 *   mongosh "$MONGO_URI" \
 *     --eval "RA292_DEPLOYED_AT='2026-08-11T09:00:00Z'" \
 *     docs/diagnostics/ra292-isnewsite-audit.js
 *
 * ─────────────────────────────────────────────────────────────────────────────
 * ⚠  ON CDP, PREFER THE IN-APP DIAGNOSTIC.
 *
 * CDP gives no way to run ad-hoc mongosh against a deployed database. This
 * script is for local and for any environment where direct DB access exists.
 * For a deployed environment use the in-app equivalent, which classifies
 * identically and reports through the log pipeline:
 *
 *   Diagnostics__Ra292IsNewSiteAudit=true   (boot, read the log, turn it off)
 *
 * ─────────────────────────────────────────────────────────────────────────────
 * THE DEFECT (epr-2uxy)
 *
 * Operator-side `OverseasSiteModel` gained `IsNewSite` with a `= true` property
 * initializer (423d27d, 2026-07-26). Applications are POCO-mapped, and a MISSING
 * BSON element leaves the C# initializer rather than `default(bool)` — so any
 * overseas site persisted before that date reads back as `true` regardless of
 * what it actually was. From 9e8e9da (2026-07-26, RA-294) that value began being
 * transmitted here, where it is frozen. RA-292 did not create this; it made it
 * regulator-visible by rendering a "New" badge from the field.
 *
 * ─────────────────────────────────────────────────────────────────────────────
 * THE DISCRIMINATOR: `orsId`
 *
 * Established from operator-backend git history, not inferred:
 *
 *   - ReEx-sourced sites have NEVER carried an `orsId`. `HttpReExApiAdapter`
 *     has never set it in any revision, and it sets `IsNewSite = false`
 *     explicitly. The stub adapter and `ApplyPromotedFields` don't set it either.
 *   - Operator-added sites ALWAYS carry one: `required` on the request,
 *     `NotEmpty().MaximumLength(10)` in the validator.
 *   - The operator serialiser uses `WhenWritingNull`, so a null `orsId` is
 *     OMITTED rather than sent as `"orsId": null`.
 *
 * Therefore, within the window:
 *
 *   orsId PRESENT + isNewSite true  ⟹ operator-added ⟹ CORRECT
 *   orsId ABSENT  + isNewSite true  ⟹ ReEx-sourced   ⟹ PROVABLY WRONG
 *
 * `orsId` entered the payload in the SAME commit that started transmitting
 * `isNewSite`, so there is no sub-window carrying the bad flag without the
 * signal beside it.
 *
 * There is deliberately NO "every site is true" heuristic here. It was only ever
 * a proxy for provenance; `orsId` gives provenance directly and per site, so the
 * heuristic adds nothing and carries a real misuse risk (an application where
 * every site genuinely IS new is ordinary — a first-time exporter, or a prior
 * year with no overseas sites).
 *
 * ─────────────────────────────────────────────────────────────────────────────
 * THE AMBIGUITY GUARD
 *
 * `orsId` is itself client-clobberable: it is `string?` on the operator model
 * and `PatchOverseasSites` replaced the site list wholesale. If a client ever
 * stripped it, an operator-added site would masquerade as ReEx-sourced, and a
 * naive correction would stamp `false` over a genuinely new site — hiding it
 * from the regulator. That is worse than the defect, which errs toward
 * over-showing.
 *
 * So a site missing `orsId` is only called PROVABLY CORRUPT when it also carries
 * none of the detail fields a ReEx-mapped site never has (contact details,
 * operation code, waste codes, address lines, BES evidence, interim site). One
 * that does carry such detail is reported as AMBIGUOUS: adjudicate by hand,
 * never auto-correct.
 *
 * ─────────────────────────────────────────────────────────────────────────────
 * NOT AFFECTED, AND DELIBERATELY NOT SCANNED
 *
 *   interimSite.isNewSite — `InterimSiteModel` and its flag were added in the
 *     same commit, so no interim site predates the flag, and ReEx never creates
 *     interim sites.
 *   prns.authorisers[].isNew — introduced by RA-292 and never previously
 *     transmitted.
 *
 * The entire remediation surface is ORS-level `isNewSite`.
 *
 * Field-name note: MongoDB stores all C# properties as camelCase due to the
 * global CamelCaseElementNameConvention registered in MongoConversions.
 */

const TARGET_DB =
  typeof RA292_TARGET_DB !== 'undefined' && RA292_TARGET_DB
    ? RA292_TARGET_DB
    : 'epr-register-case-management'

const collection = db.getSiblingDB(TARGET_DB).getCollection('workItems')

// ─────────────────────────────────────────────────────────────────────────────
// Read-only guard.
//
// This is a diagnostic run against production data by someone who may not have
// read the source first, so "it happens not to call any writes" is not a strong
// enough promise. Every mutating method on the collection handle is replaced
// with a throw, which makes the read-only property structural: an edit that
// later introduces a write fails loudly on the first run instead of quietly
// modifying work items.
//
// Belt-and-braces, not the primary control. The primary control is to run this
// with a read-only database user.
// ─────────────────────────────────────────────────────────────────────────────
const MUTATING_METHODS = [
  'insert', 'insertOne', 'insertMany',
  'update', 'updateOne', 'updateMany',
  'replaceOne', 'save',
  'remove', 'deleteOne', 'deleteMany',
  'findOneAndUpdate', 'findOneAndReplace', 'findOneAndDelete',
  'bulkWrite', 'drop', 'dropIndex', 'dropIndexes', 'createIndex',
  'renameCollection'
]
for (const method of MUTATING_METHODS) {
  collection[method] = function () {
    throw new Error(
      `ra292-isnewsite-audit is a READ-ONLY diagnostic; refusing to call ` +
        `${method}(). Remediation belongs in the reviewed, gated ` +
        `ReAccreditationIsNewSiteCorrectionMigration, not in this script.`
    )
  }
}

// Window. The date bound is the PRIMARY filter.
//   Start: 2026-07-26 (9e8e9da) — first build that transmitted isNewSite at all.
//   End:   the RA-292 deploy. The window CLOSES once the operator model default
//          is corrected, so the candidate set is finite and cannot grow.
//          Defaults to now, which over-reports rather than under-reports.
const WINDOW_START = new Date('2026-07-26T00:00:00Z')
const WINDOW_END =
  typeof RA292_DEPLOYED_AT !== 'undefined' && RA292_DEPLOYED_AT
    ? new Date(RA292_DEPLOYED_AT)
    : new Date()

// Fields HttpReExApiAdapter.MapOverseasSite never populates. Presence on a site
// with no orsId means operator-entered-with-orsId-stripped, not ReEx-sourced.
const OPERATOR_ENTERED_DETAIL = [
  'contactName', 'contactEmail', 'contactPhone',
  'operationCode', 'code1', 'code2', 'code3',
  'addressLine1', 'addressLine2', 'townOrCity', 'coordinates',
  'repatriatedLoads', 'conditionsOfExport', 'besEvidence', 'interimSite'
]

function classifySite(site) {
  if (site.isNewSite !== true) return 'NOT_FLAGGED_NEW'
  const orsId = site.orsId
  if (orsId !== undefined && orsId !== null && String(orsId).trim() !== '') {
    return 'OPERATOR_ADDED_CORRECT'
  }
  const hasDetail = OPERATOR_ENTERED_DETAIL.some(
    (f) => site[f] !== undefined && site[f] !== null
  )
  return hasDetail ? 'AMBIGUOUS_ORSID_MISSING' : 'PROVABLY_CORRUPT'
}

function idOf(item) {
  const raw = item._id
  return raw && typeof raw.toString === 'function' ? raw.toString() : String(raw)
}

function sitesOf(item) {
  const sites =
    item.payload && item.payload.overseasSites
      ? item.payload.overseasSites.sites
      : undefined
  return Array.isArray(sites) ? sites : []
}

const provablyCorrupt = []
const ambiguous = []
let itemsScanned = 0
let sitesCorrupt = 0
let sitesAmbiguous = 0
let sitesCorrect = 0
let sitesNotNew = 0

collection
  .find({
    typeId: 're-accreditation',
    submittedAt: { $gte: WINDOW_START, $lt: WINDOW_END },
    'payload.overseasSites.sites.0': { $exists: true }
  })
  .sort({ submittedAt: 1 })
  .forEach((item) => {
    itemsScanned++
    const sites = sitesOf(item)
    const verdicts = sites.map((s, i) => ({
      index: i,
      siteName: s.siteName,
      orsId: s.orsId === undefined ? '(absent)' : s.orsId,
      verdict: classifySite(s)
    }))

    for (const v of verdicts) {
      if (v.verdict === 'PROVABLY_CORRUPT') sitesCorrupt++
      else if (v.verdict === 'AMBIGUOUS_ORSID_MISSING') sitesAmbiguous++
      else if (v.verdict === 'OPERATOR_ADDED_CORRECT') sitesCorrect++
      else sitesNotNew++
    }

    const row = {
      id: idOf(item),
      applicationReference:
        (item.payload && item.payload.applicationReference) || '(none)',
      organisationName:
        (item.payload && item.payload.organisationName) || '(none)',
      submittedAt: item.submittedAt,
      sites: verdicts
    }

    if (verdicts.some((v) => v.verdict === 'PROVABLY_CORRUPT')) {
      provablyCorrupt.push(row)
    }
    if (verdicts.some((v) => v.verdict === 'AMBIGUOUS_ORSID_MISSING')) {
      ambiguous.push(row)
    }
  })

print('')
print('═══════════════════════════════════════════════════════════════════════')
print(' epr-2uxy — isNewSite audit (READ-ONLY, nothing written)')
print('═══════════════════════════════════════════════════════════════════════')
print(` Database     : ${TARGET_DB}.workItems`)
print(` Window start : ${WINDOW_START.toISOString()}  (9e8e9da, first transmission)`)
print(` Window end   : ${WINDOW_END.toISOString()}${
  typeof RA292_DEPLOYED_AT !== 'undefined' && RA292_DEPLOYED_AT
    ? '  (supplied RA-292 deploy time)'
    : '  (now — pass RA292_DEPLOYED_AT for a precise bound)'
}`)
print('')
print(` In-window re-accreditation items with overseas sites : ${itemsScanned}`)
print('')
print(` SITES provably corrupt  (true, no orsId, no detail)  : ${sitesCorrupt}`)
print(`   across items                                       : ${provablyCorrupt.length}`)
print(` SITES ambiguous (no orsId BUT operator detail)       : ${sitesAmbiguous}`)
print(`   across items                                       : ${ambiguous.length}`)
print(` SITES correctly new (operator-added, orsId present)  : ${sitesCorrect}`)
print(` SITES not flagged new                                : ${sitesNotNew}`)
print('')

if (sitesCorrupt === 0 && sitesAmbiguous === 0) {
  print(' RESULT: nothing at risk in this environment.')
  print(' Record the environment and this figure on epr-2uxy.')
} else {
  if (provablyCorrupt.length > 0) {
    print('─── PROVABLY CORRUPT: correctable by the gated migration ──────────────')
    printjson(provablyCorrupt)
    print('')
  }
  if (ambiguous.length > 0) {
    print('─── ⚠  AMBIGUOUS: DO NOT auto-correct. Adjudicate by hand. ────────────')
    print('    orsId is missing but operator-entered detail is present, so these')
    print('    may be operator-added sites whose orsId was stripped rather than')
    print('    ReEx-sourced ones. Correcting them would hide a genuinely new site')
    print('    from the regulator. The migration refuses to touch these.')
    printjson(ambiguous)
    print('')
  }
}

print('═══════════════════════════════════════════════════════════════════════')
print(' Nothing was modified. See docs/diagnostics/ra292-isnewsite-audit.md')
print('═══════════════════════════════════════════════════════════════════════')
