/**
 * Migration: v4 → v5  —  remove explicit duly-make action, auto-transition
 * submitted→duly-made items that have all tasks complete, start the SLA clock,
 * and backfill the SLA clock for items already stuck in duly-made without one.
 *
 * Run with mongosh against the case-management database:
 *
 *   mongosh "mongodb://localhost:27017/epr-register-case-management" \
 *     migrate-v4-to-v5-auto-duly-made.js
 *
 * What it does:
 *
 *  Pass 1 — snapshot patch + optional auto-transition:
 *    For every re-accreditation work item whose frozen template snapshot
 *    still contains the "duly-make" transition:
 *      - Remove that transition and bump templateVersion to "v5" so the
 *        action button no longer appears in the UI.
 *      - If the item is still in "submitted" state and both submitted-state
 *        tasks are complete, additionally:
 *          • Set stateId to "duly-made"
 *          • Set slaClock.startedAt = now (clock starts at transition time)
 *          • Append "action-applied" and "sla-clock-started" audit entries.
 *
 *  Pass 2 — SLA clock backfill:
 *    For any re-accreditation item already in "duly-made" whose slaClock is
 *    null (transitioned before this migration ran), set slaClock.startedAt
 *    to lastModifiedAt (the time of the transition) and append a
 *    "sla-clock-started" audit entry.
 *
 * Safe to re-run — items already on v5 are skipped by Pass 1; items with a
 * non-null slaClock are skipped by Pass 2.
 *
 * Field-name note: MongoDB stores all C# properties as camelCase due to the
 * global CamelCaseElementNameConvention registered in MongoConversions.
 */

const TARGET_DB = 'epr-register-case-management'
const collection = db.getSiblingDB(TARGET_DB).getCollection('workItems')

// 84 days expressed as .NET ticks (1 tick = 100 ns).
// Stored in the "targetDuration" BSON field of WorkItemSlaClock.
const TARGET_DURATION_TICKS = NumberLong('72576000000000')

const SUBMITTED_TASK_IDS = [
  'verify-organisation-details',
  'confirm-application-completeness'
]

function allSubmittedTasksComplete(item) {
  // Prefer the canonical per-task status map (epr-gl6 dual-write).
  const stateMap = (item.taskStatusesByState || {})[item.stateId]
  if (stateMap) {
    return SUBMITTED_TASK_IDS.every((id) => stateMap[id] === 'Completed')
  }
  // Fall back to the legacy completed-task-ids bucket.
  const bucket = ((item.completedTaskIdsByState || {})[item.stateId] || [])
  return SUBMITTED_TASK_IDS.every((id) => bucket.includes(id))
}

function auditEntry(action, actionDisplayName, details, createdAt) {
  return {
    _id: UUID().toString(),
    action,
    actionDisplayName,
    details,
    createdAt,
    createdBy: 'migration',
    createdByName: 'Migration: v4→v5'
  }
}

let stripped = 0
let autoTransitioned = 0
let slaBackfilled = 0
let errors = 0

// ─────────────────────────────────────────────────────────────────────────────
// Pass 1: patch snapshot + auto-transition submitted items
// ─────────────────────────────────────────────────────────────────────────────
collection
  .find({
    typeId: 're-accreditation',
    'templateSnapshot.transitions': { $elemMatch: { actionId: 'duly-make' } }
  })
  .forEach((item) => {
    try {
      const newTransitions = (item.templateSnapshot?.transitions || []).filter(
        (t) => t.actionId !== 'duly-make'
      )

      const update = {
        $set: {
          'templateSnapshot.transitions': newTransitions,
          'templateSnapshot.templateVersion': 'v5',
          templateVersion: 'v5'
        }
      }

      if (item.stateId === 'submitted' && allSubmittedTasksComplete(item)) {
        const now = new Date()
        update.$set.stateId = 'duly-made'
        update.$set.lastModifiedAt = now
        update.$set.slaClock = {
          startedAt: now,
          targetDuration: TARGET_DURATION_TICKS,
          breached: false
        }
        update.$push = {
          auditLog: {
            $each: [
              auditEntry(
                'action-applied',
                'Action applied',
                {
                  actionId: 'duly-make',
                  actionDisplayName: 'Mark as duly made',
                  fromStateId: 'submitted',
                  toStateId: 'duly-made'
                },
                now
              ),
              auditEntry(
                'sla-clock-started',
                'SLA clock started',
                { startedAt: now.toISOString(), targetDays: '84' },
                now
              )
            ]
          }
        }
        autoTransitioned++
      }

      collection.updateOne({ _id: item._id }, update)
      stripped++
    } catch (e) {
      print(`ERROR (pass 1) item ${item._id}: ${e}`)
      errors++
    }
  })

// ─────────────────────────────────────────────────────────────────────────────
// Pass 2: backfill SLA clock for items already in duly-made without one
// ─────────────────────────────────────────────────────────────────────────────
collection
  .find({
    typeId: 're-accreditation',
    stateId: 'duly-made',
    slaClock: null
  })
  .forEach((item) => {
    try {
      // Use lastModifiedAt as startedAt — that timestamp records when the
      // item was last written, which is when the hook transitioned it.
      const startedAt = item.lastModifiedAt || new Date()
      const now = new Date()

      collection.updateOne(
        { _id: item._id },
        {
          $set: {
            slaClock: {
              startedAt,
              targetDuration: TARGET_DURATION_TICKS,
              breached: false
            }
          },
          $push: {
            auditLog: auditEntry(
              'sla-clock-started',
              'SLA clock started',
              { startedAt: startedAt.toISOString(), targetDays: '84' },
              now
            )
          }
        }
      )
      slaBackfilled++
    } catch (e) {
      print(`ERROR (pass 2) item ${item._id}: ${e}`)
      errors++
    }
  })

print('Migration complete.')
print(`  Snapshot patched to v5               : ${stripped}`)
print(`  Auto-transitioned submitted→duly-made: ${autoTransitioned}`)
print(`  SLA clock backfilled (duly-made)      : ${slaBackfilled}`)
print(`  Errors                                : ${errors}`)
