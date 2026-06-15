/**
 * Migration: de-duplicate payload.applicationReference
 *
 * Fixes a fatal startup crash. RA-219 (commit 52ab155b) tightened the index on
 * payload.applicationReference to UNIQUE + SPARSE. Any environment whose
 * workItems collection holds two or more documents sharing the same
 * applicationReference (e.g. legacy client-supplied values such as
 * "RA-2024-00123" that predate server-side generation) can no longer build the
 * index — MongoDB returns E11000 and the application aborts on startup:
 *
 *   Index build failed ... index: payload.applicationReference_1
 *   dup key: { payload.applicationReference: "RA-2024-00123" }
 *
 * This script makes each applicationReference unique again WITHOUT deleting any
 * work item. Within every group of duplicates it keeps the OLDEST document
 * (earliest createdAt) unchanged and assigns every other document a fresh,
 * collision-checked reference in the canonical server-generated format
 * (^RA-\d{9}$ — see ApplicationReferenceGenerator). An audit-log entry records
 * each reassignment.
 *
 * Run with mongosh against the case-management database. Set TARGET_DB to match
 * the environment's Mongo:DatabaseName (CDP/dev uses appsettings.json's
 * "epr-register-enrol-management-be"; local compose uses
 * "epr-register-case-management"). Override at the CLI with --eval, e.g.:
 *
 *   mongosh "mongodb://localhost:27017/" \
 *     --eval 'var TARGET_DB="epr-register-enrol-management-be"' \
 *     dedupe-application-reference.js
 *
 * After it reports "0 duplicate groups remaining" the unique index will build
 * cleanly and the service will start.
 *
 * Safe to re-run — once references are unique it finds nothing to do and is a
 * no-op.
 *
 * Field-name note: MongoDB stores all C# properties as camelCase due to the
 * global CamelCaseElementNameConvention registered in MongoConversions.
 */

// eslint-disable-next-line no-undef
const TARGET_DB =
  typeof globalThis !== 'undefined' && globalThis.TARGET_DB
    ? globalThis.TARGET_DB
    : 'epr-register-enrol-management-be'

const collection = db.getSiblingDB(TARGET_DB).getCollection('workItems')

print(`De-duplicating payload.applicationReference in ${TARGET_DB}.workItems`)

// ─────────────────────────────────────────────────────────────────────────────
// Build the set of references already in use so regenerated values never clash
// with an existing reference (kept or freshly assigned).
// ─────────────────────────────────────────────────────────────────────────────
const usedReferences = new Set()
collection
  .find(
    { 'payload.applicationReference': { $type: 'string' } },
    { 'payload.applicationReference': 1 }
  )
  .forEach((doc) => usedReferences.add(doc.payload.applicationReference))

function generateUniqueReference() {
  // Mirror ApplicationReferenceGenerator: "RA-" + 9-digit zero-padded number.
  // mongosh has no crypto RNG; Math.random is adequate for a one-off backfill
  // and every candidate is collision-checked against usedReferences anyway.
  for (let attempt = 0; attempt < 1000; attempt++) {
    const suffix = Math.floor(Math.random() * 1000000000)
      .toString()
      .padStart(9, '0')
    const candidate = `RA-${suffix}`
    if (!usedReferences.has(candidate)) {
      usedReferences.add(candidate)
      return candidate
    }
  }
  throw new Error('Could not generate a unique applicationReference after 1000 attempts')
}

function auditEntry(oldReference, newReference, createdAt) {
  return {
    _id: UUID().toString(),
    action: 'application-reference-reassigned',
    actionDisplayName: 'Application reference reassigned',
    details: {
      reason: 'duplicate-application-reference-dedupe',
      previousApplicationReference: oldReference,
      applicationReference: newReference
    },
    createdAt,
    createdBy: 'migration',
    createdByName: 'Migration: dedupe-application-reference'
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Find every applicationReference shared by more than one document.
// ─────────────────────────────────────────────────────────────────────────────
const duplicateGroups = collection
  .aggregate([
    { $match: { 'payload.applicationReference': { $type: 'string' } } },
    {
      $group: {
        _id: '$payload.applicationReference',
        ids: { $push: { id: '$_id', createdAt: '$createdAt' } },
        count: { $sum: 1 }
      }
    },
    { $match: { count: { $gt: 1 } } }
  ])
  .toArray()

print(`Found ${duplicateGroups.length} duplicate applicationReference group(s).`)

let reassigned = 0
let errors = 0

duplicateGroups.forEach((group) => {
  // Keep the oldest document; sort missing createdAt last so a real timestamp
  // always wins the "keep" slot.
  const members = group.ids.slice().sort((a, b) => {
    const at = a.createdAt ? a.createdAt.getTime() : Infinity
    const bt = b.createdAt ? b.createdAt.getTime() : Infinity
    return at - bt
  })

  // members[0] is kept as-is; reassign the rest.
  for (let i = 1; i < members.length; i++) {
    const member = members[i]
    try {
      const now = new Date()
      const newReference = generateUniqueReference()
      collection.updateOne(
        { _id: member.id },
        {
          $set: { 'payload.applicationReference': newReference },
          $push: { auditLog: auditEntry(group._id, newReference, now) }
        }
      )
      print(`  ${member.id}: ${group._id} -> ${newReference}`)
      reassigned++
    } catch (e) {
      print(`ERROR item ${member.id} (was ${group._id}): ${e}`)
      errors++
    }
  }
})

// ─────────────────────────────────────────────────────────────────────────────
// Verify nothing is left for the unique index to choke on.
// ─────────────────────────────────────────────────────────────────────────────
const remaining = collection
  .aggregate([
    { $match: { 'payload.applicationReference': { $type: 'string' } } },
    { $group: { _id: '$payload.applicationReference', count: { $sum: 1 } } },
    { $match: { count: { $gt: 1 } } },
    { $count: 'groups' }
  ])
  .toArray()

const remainingGroups = remaining.length > 0 ? remaining[0].groups : 0

print('De-dupe complete.')
print(`  Duplicate groups found      : ${duplicateGroups.length}`)
print(`  References reassigned        : ${reassigned}`)
print(`  Errors                       : ${errors}`)
print(`  Duplicate groups remaining   : ${remainingGroups}`)

if (remainingGroups === 0) {
  print('OK — the unique payload.applicationReference index can now build.')
} else {
  print('WARNING — duplicate groups remain; investigate before restarting the service.')
}
