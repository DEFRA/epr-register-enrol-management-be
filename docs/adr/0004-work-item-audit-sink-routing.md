# ADR-0004: Work-item audit sink — single on-document log, no parallel CDP audit channel

**Date:** 2026-04-30
**Status:** Accepted
**Issue:** epr-g25

## Context

The backend currently has two audit-shaped channels:

1. **`WorkItem.AuditLog`** — an append-only `List<WorkItemAuditEntry>`
   persisted inline on each work-item document in MongoDB. The work-item
   engine (`WorkItems/Core/WorkItemService` and `WorkItemPersistence`)
   appends an entry on every successful mutation: `work-item-submitted`,
   `task-completed`, `action-applied`, `assigned`, `unassigned`,
   `note-added`. Each entry carries the action id, contextual details,
   the actor (`user:id` / `user:name` snapshotted at write time), and a
   `TimeProvider`-sourced `CreatedAt`. The submission entry is written
   in the same `CreateAsync` call as the document itself, so a work item
   and its birth event land in storage atomically. The framework owns
   this — modules cannot opt in or out.

   This gives us:
   - per-document, queryable, replayable history;
   - identical shape across every work-item type;
   - retention tied to the document itself, not log-rotation policy;
   - no second source of truth that can drift.

2. **`Utils/Auditing/AuditLogger`** — a Serilog sub-logger that filters on
   the `IsAudit` log property and writes those events to a separate
   console sink with `log.level=audit`. The main ECS logger filters the
   same events OUT, so audit-tagged events appear only on the audit
   sink. There are **no production call sites** that set `IsAudit=true`;
   the helper is plumbed into `CdpLogging.Configuration` but inert. The
   agent-mode contract for this repo and `docs/work-items.md` both call
   it deprecated.

CDP itself, per the in-repo platform docs
([`docs/cdp-deployment.md`](../cdp-deployment.md),
[`docs/cdp-tracing.md`](./cdp-tracing.md)) and the platform behaviour we
already rely on (Serilog ECS to stdout → CloudWatch Logs, trace-id
propagation via `x-cdp-request-id`), does not at PoC stage publish a
contract that requires services to forward business audit events to a
separate platform audit sink. Operational logs flow through the standard
ECS pipeline. No CDP-supplied .NET package equivalent to
`@defra/cdp-auditing` exists today — this is the same gap recorded in
ADR-0002 for metrics. External CDP documentation was not fetched for
this ADR; the in-repo docs are sufficient to confirm there is no
contract to satisfy.

The companion frontend BFF consumes `@defra/cdp-auditing` (Node), but
that is a frontend concern (login / session events tied to the BFF's own
user-facing surface) and does not impose a backend contract.

## Decision

**Adopt option (A): single sink.**

`WorkItem.AuditLog` is the **only** authoritative audit record for
work-item mutations. The engine does not, and will not, emit a parallel
audit-tagged Serilog event for the same mutation. Operational
`LogInformation` lines describing what the engine did remain — they
serve support diagnostics, not the legal/replayable audit story.

Specifically:

- The engine continues to append `WorkItemAuditEntry` to
  `WorkItem.AuditLog` on every successful mutation, exactly as
  `docs/work-items.md` describes. No change.
- No engine code path calls `logger.Audit(...)` or sets the `IsAudit`
  property. New call sites are forbidden.
- The `Utils/Auditing/AuditLogger` helper and its Serilog sub-pipeline in
  `CdpLogging.Configuration` are formally accepted as **dead code** by
  this ADR and scheduled for removal as a follow-up. Removal is **not**
  performed in this ADR's scope (per the issue brief).

This decision will be revisited if any of the following become true:

- CDP publishes a documented audit-sink contract (.NET package or
  required log-event schema) that this service must satisfy.
- A compliance requirement appears that demands audit events be
  centralised across services for cross-service correlation or
  long-term retention beyond what the work-item document gives us.
- Work-item documents acquire a deletion / TTL story that would erase
  the on-document audit trail.

If any of those triggers fire, this ADR should be **superseded** by a
new ADR that re-evaluates options (B) — dual-sink fire-and-forget — and
(C) — dual-sink durable via an event store / SNS topic. Both were
considered here and rejected on the grounds below.

### Options considered and rejected

- **(B) Dual sink, fire-and-forget.** Engine writes
  `WorkItem.AuditLog` AND emits a tagged ECS log line per mutation.
  Rejected: doubles the failure surface (a successful mutation can now
  produce a missing audit log line without the operator noticing),
  produces a second source of truth that can diverge from the document,
  and gains nothing today because no CDP consumer is reading the tagged
  stream.
- **(C) Dual sink, durable.** Engine publishes audit events to a
  separate event store / SNS topic transactionally with the document
  write. Rejected as premature: requires distributed-transaction
  reasoning (outbox pattern or two-phase commit) for a guarantee no
  current consumer needs.

## Consequences

### Positive

- One authoritative audit trail per work item; no divergence between
  channels.
- No dependency on Serilog sink reliability for the auditable record —
  if the document write succeeds the audit entry is there, and vice
  versa.
- No new platform-specific audit dependency to maintain at PoC stage,
  consistent with ADR-0002's posture on metrics.
- Test assertions stay simple: integration tests already assert against
  the persisted `WorkItem.AuditLog` via `WorkItemPersistence`, not
  against log output.

### Negative

- Audit data is per-document. Cross-service or platform-wide audit
  queries are not possible from a CDP central audit view today; an
  operator wanting "everything user X did" must query Mongo
  (`AuditLog.CreatedBy = X` across the `work-items` collection) rather
  than a centralised log index.
- Long-retention compliance scenarios (e.g. archive every audit event
  for N years) would need to be solved at the database layer (Mongo
  backups / export) rather than by routing to a CDP audit sink. This
  is an explicit accepted trade-off at PoC stage.

### Neutral

- The deprecated `Utils/Auditing/AuditLogger` is now formally dead code
  awaiting removal under a follow-up issue. Until that issue lands, the
  Serilog audit sub-logger continues to be wired but never receives
  events.
- Operational ECS logs continue unchanged. Trace-id correlation
  (`x-cdp-request-id`) still ties operator-facing log lines to a
  request, and through the request to the work-item id printed in those
  lines.

## Verification

A reviewer can confirm this decision is in force by checking:

1. **No engine code emits audit-tagged Serilog events.** A repo-wide
   search for `IsAudit`, `AuditPropertyName`, or `logger.Audit(`
   returns matches only inside `Utils/Auditing/` and its registration
   in `Utils/Logging/CdpLogging.cs` — no production call sites.
2. **Every successful engine mutation writes a `WorkItemAuditEntry`.**
   `WorkItems/Core/WorkItemService` is the single place that appends to
   `WorkItem.AuditLog`, and the integration tests under
   `EprRegisterEnrolManagementBe.Test/WorkItems/` assert the persisted
   list directly (not log output).
3. **Idempotent no-ops do not write entries** (re-completing a task,
   re-assigning the same user, re-unassigning an unassigned item),
   per the rules in `docs/work-items.md` — those tests would fail if
   we ever introduced a parallel emit-on-attempt sink.
4. **The deprecation note in the agent contract and
   `docs/work-items.md` matches the code state**: `AuditLogger` exists
   but is referenced only by `CdpLogging.Configuration` and has no
   callers under `WorkItems/`.

## Follow-up

A separate bd issue should be filed to **delete `Utils/Auditing/`
(`AuditLogger.cs`, `AuditLoggerExtension.cs`) and remove its wiring
from `Utils/Logging/CdpLogging.cs`**. That issue is not part of this
ADR's scope; this ADR's job is to record the decision that makes the
removal safe.
