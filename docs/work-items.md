# Work item framework (backend)

The case management system manages **work items**. Re-accreditation is one
type, but the design must accommodate other types in future without rewrites.
This document describes the framework that makes adding a new work item type
a localised change to a new module.

## Goals

- Each work item type is **self-contained** in its own module.
- The core application provides the framework; modules provide the behaviour.
- **Adding a new type is one folder + one line in `Program.cs`.** No other
  module changes; no core changes beyond the registration call.
- It is obvious from reading a module which **tasks** are required for each
  **state** of the work item.

## Building blocks

Defined in `EprRegisterEnrolManagementBe/WorkItems/Core/`:

| Type | Purpose |
| --- | --- |
| `WorkItemState` | Identifier + display name for a state. `IsTerminal` marks completion states (e.g. approved/rejected). |
| `WorkItemTask` | Identifier + display name for a unit of work to be completed in a state. |
| `WorkItemTransition` | A named action (`approve`, `reject`, `withdraw`) that moves a work item from one state to another. `RequiresAllTasksComplete` (default `true`) gates the action behind every task for the from-state being marked complete. |
| `WorkItemTaskProgress` | A task's id + display name + whether it is complete for the work item's current state. |
| `IWorkItemType` | Declares a type's `TypeId`, `DisplayName`, `InitialState`, `States`, `GetTasksForState(stateId)` and `Transitions`. Pure & side-effect free. |
| `IWorkItemModule` | A module's entry point. Exposes the `Type` and contributes `RegisterServices(services)` and `MapEndpoints(endpoints)`. |
| `IWorkItemRegistry` | DI-resolvable lookup of every registered type. |
| `IWorkItemService` | Framework service object that drives task completion and state transitions. Resolves the work item, validates the request against the type, persists the change and writes an audit log. |
| `WorkItem` | The persisted work item envelope: id, type id, state id, submitted-at, last-modified-at, submitted-by (CDP Cognito client id), per-state completed task ids, free-form payload. |
| `IWorkItemPersistence` | Framework-owned MongoDB persistence for `WorkItem`s. |
| `WorkItemModuleExtensions` | `AddWorkItemFramework()`, `AddWorkItemModule<T>()`, `MapWorkItemModules()`, `MapWorkItemFrameworkEndpoints()`. |

> The **task and state engine** validates progressions, enforces task
> completion before transitions and handles the corresponding HTTP requests.
> Module-specific business logic belongs in module service objects which
> may call (or wrap) `IWorkItemService`.

## Ingestion API

The framework exposes generic, type-agnostic endpoints for accepting,
listing and progressing work items. They live in `WorkItemEndpoints` and
are mounted by `MapWorkItemFrameworkEndpoints()`:

| Method | Route | Description |
| --- | --- | --- |
| `POST` | `/work-items` | Submit a new work item. Body: `{ "typeId": "<type>", "payload": { ... } }`. The `typeId` must be registered with the framework; the server stamps the item with the type's `InitialState`, the caller's CDP Cognito client id and a server-side timestamp. Returns `201 Created` with `Location: /work-items/{id}`. |
| `GET` | `/work-items/{id}` | Fetch a single work item by id, projected with current task progress and the actions the engine will currently allow. |
| `GET` | `/work-items` | List persisted work items (with the same projection), newest first, with filter / search / pagination per RA-93. Query string parameters: `typeId` (repeatable), `stateId` (repeatable), `search` (free-text — matched on id and submitter), `page` (1-based, default 1), `pageSize` (default 20, capped at 100). Returns a paged envelope: `{ items, totalCount, page, pageSize }`. |
| `POST` | `/work-items/{id}/tasks/{taskId}/complete` | Mark a task complete on the work item's current state. Idempotent. `400` if the task does not apply to the current state, `404` if the work item is unknown. |
| `POST` | `/work-items/{id}/actions/{actionId}` | Invoke a named action declared by the type's transitions. `409` if the work item is in a terminal state or has outstanding tasks; `400` for unknown actions or transitions whose from-state does not match. |

All three endpoints require authentication via the CDP Cognito client id
header (`x-cdp-cognito-client-id`) per RA-89/RA-85b.

The persisted envelope is owned by the framework; modules describe the shape
of their payload via their `IWorkItemType` and operate on it via their own
service objects.

## Adding a new work item type

1. **Create a folder** under `EprRegisterEnrolManagementBe/WorkItems/<TypeName>/` containing:

   ```
   WorkItems/MyType/
     MyType.cs            // implements IWorkItemType
     MyTypeModule.cs      // implements IWorkItemModule
     Endpoints/           // module-scoped HTTP endpoints
     Services/            // module-scoped service objects
     Models/              // module-scoped models
   ```

2. **Implement `IWorkItemType`**, declaring states and tasks-per-state. Make
   the static structure obvious from a glance; if tasks depend on data, return
   a dynamically-built collection from `GetTasksForState` — but keep the
   declaration co-located with the type.

3. **Implement `IWorkItemModule`** to register the module's services and
   endpoints. Mount endpoints under `/work-items/<type-id>/...` to keep
   modules isolated from each other.

4. **Register the module** in `Program.cs`:

   ```csharp
   static void ConfigureWorkItems(IServiceCollection services)
   {
       services.AddWorkItemFramework();
       services.AddWorkItemModule<MyTypeModule>();   // <-- one line per module
   }
   ```

   `MapWorkItemModules()` is already invoked from `ConfigureEndpoints`.

That is the complete list of changes required outside the new module folder.

## Conventions

- A module **must not** depend on another module. If two modules need shared
  behaviour, lift it into the framework (or a clearly shared utility under
  `EprRegisterEnrolManagementBe/Utils`).
- Module DI registrations should use **module-scoped interfaces**
  (`IMyTypePersistence`, not `IPersistence`) to avoid colliding with other
  modules.
- A module's HTTP routes should namespace themselves under
  `/work-items/<type-id>` so they do not clash with another module's routes.
- Treat `IWorkItemType` as data: no I/O, no DI dependencies. Behaviour
  belongs in service objects registered via `RegisterServices`.
- Form submissions (task completion, state transitions, type-specific
  payload edits) **must** flow through service objects. The framework's
  `IWorkItemService` covers task completion and transitions; module-scoped
  services should follow the same pattern (intent-named methods, return
  result objects rather than raw exceptions).

## Template versioning (RA-94)

Work items live for a long time. Their state machine, tasks and detail
templates evolve. Once a work item has been progressed by an assessor under
v1 of a type, the audit history must continue to make sense even after the
team ships v2. The framework solves this by **freezing the template at
submission**.

### Wiring

`IWorkItemTemplate` is the slice of `IWorkItemType` the engine actually
needs at runtime: `States`, `Transitions`, `GetTasksForState(stateId)` and
a `TemplateVersion` string. `IWorkItemType` extends `IWorkItemTemplate`.

`WorkItemTemplateSnapshot` is a sealed, frozen `IWorkItemTemplate` produced
from a live `IWorkItemType` via `WorkItemTemplateSnapshot.Capture(type)`.
`Capture` walks every state, evaluates `GetTasksForState` and stores the
result in an in-memory dictionary so the snapshot does not call back into
the live type after capture.

### Storage

`WorkItem` carries two new fields:

- `TemplateSnapshot` — the captured `IWorkItemTemplate` for the version of
  the type that submitted the work item.
- `TemplateVersion` — a copy of the snapshot's version string for cheap
  filtering and surfaceing on the wire.

Both are populated by `POST /work-items` before the envelope is persisted.

### Engine resolution

`WorkItemService.ResolveTemplate(workItem)` returns the work item's
`TemplateSnapshot` if present, otherwise it falls back to the live
`IWorkItemType` from the registry (so legacy items submitted before this
change continue to work). Every engine operation — task completion,
action validation, projection of tasks and available actions — runs
against the resolved template, never the live type. As a result, shipping
v2 of a type cannot retroactively change the rules under which an in-flight
v1 work item was being progressed.

### Wire format

`WorkItemResponse` includes `templateVersion`. Clients use it (together
with `typeId`) to pick the correct detail template for the work item.

## Assignment (RA-95)

Work items can be assigned to a user. Two roles drive what a caller can do:

- `assign` — can assign or re-assign any work item to any user, and can
  unassign.
- `standard` — can only **self-assign** an unassigned work item to itself.
  Cannot re-assign other people's work, cannot take an item that is already
  owned, cannot unassign.

Both rules are enforced by `WorkItemService.AssignAsync` /
`UnassignAsync`. The frontend BFF exposes both UI affordances and the role
gates, but the backend is the source of truth: a hand-crafted POST from a
standard user that targets someone else's id returns `403`.

### Identity from the BFF

The Cognito auth handler (`CognitoClientIdAuthenticationHandler`)
optionally reads three headers forwarded by the BFF and turns them into
`ClaimsPrincipal` claims:

| Header | Claim |
| --- | --- |
| `x-cdp-user-id` | `user:id` (used by `ResolveActorUserId`) |
| `x-cdp-user-name` | `user:name` |
| `x-cdp-user-roles` | one `ClaimTypes.Role` per comma-separated value |

`User.IsInRole("assign")` therefore works as expected on every endpoint.

### Storage

`WorkItem` gains four nullable fields:

- `AssignedToId` / `AssignedToName` — the current assignee (snapshot of the
  display name so the UI does not have to look the user up again).
- `AssignedAt` — UTC timestamp of the most recent assignment change.
- `AssignedBy` — id of the user who performed the assignment (for audit).

The Mongo collection has an `assigneeAndSubmitted` index for the common
"my work" / "unassigned" list queries.

### List filters

`GET /work-items` accepts two extra query parameters:

| Param | Effect |
| --- | --- |
| `assigneeId=<userId>` | only items currently assigned to that user |
| `unassigned=true` | only items with no current assignee |

If both are supplied, `assigneeId` wins.

### Endpoints

```
POST /work-items/{id}/assign
  Body: { "assigneeId": "<userId>", "assigneeName": "<display name>" }
  Authorization: any authenticated user; backend enforces the
                 self-assign-only rule for non-`assign` callers.

POST /work-items/{id}/unassign
  Body: (none)
  Authorization: caller must hold the `assign` role.
```

Both endpoints are idempotent: assigning an item to its current assignee
or unassigning an already-unassigned item returns the existing state with
no audit churn.

## Notes (RA-96)

Every work item carries an append-only list of free-text **notes** —
short narratives an assessor records to explain context or decisions.
Notes are framework-level so every type behaves identically; modules do
not opt in.

### Storage

`WorkItem.Notes` is a `List<WorkItemNote>` persisted inline on the work
item document. A `WorkItemNote` carries:

| Field | Purpose |
| --- | --- |
| `Id` | Server-generated GUID. |
| `Text` | Note body. Trimmed at write; rendered verbatim by clients (templates must escape). |
| `CreatedAt` | UTC timestamp set by `WorkItemService` from the injected `TimeProvider`. |
| `CreatedBy` | Snapshot of the actor's user id (`user:id` claim, falling back to the Cognito client id). |
| `CreatedByName` | Snapshot of the actor's display name (`user:name` claim) at write time, so the audit narrative survives directory changes. |

Notes are stored in insertion order. The wire projection in
`WorkItemResponse.Notes` is sorted **newest-first** so a UI can render
without re-sorting.

### Endpoint

```
POST /work-items/{id}/notes
  Body: { "text": "<note body>" }
  Authorization: any authenticated user (notes are an audit narrative;
                 no role gate beyond authentication).

  Validation:
   - `text` required, non-blank, ≤ WorkItemService.MaxNoteLength (4000)
     characters. Server trims leading/trailing whitespace before storing.

  Returns: the updated WorkItemResponse (with the new note included
  newest-first under `notes`).
```

### Conventions

- **Append-only.** The framework deliberately does not expose edit or
  delete endpoints — notes are part of the audit trail.
- **No module-specific note types.** If a module needs structured per-note
  metadata, lift the requirement into the framework rather than modelling
  it on the payload.
- The note's author identity is **always** snapshotted on write. Do not
  rely on `CreatedBy` being a live foreign key into a user directory.

## Audit log (RA-97)

### Single source of truth

`WorkItem.AuditLog` (the per-document, append-only list described below) is
the **single authoritative audit trail** for every work item. There is no
parallel database or external audit store. The console-only
`AuditLogger`/`logger.Audit(...)` helper that previously emitted a second,
in-memory-only stream of audit events has been retired — it left no durable
record (lost on log rotation) and produced two divergent histories for the
same mutation. Service operational logs at `LogInformation` describe what
the engine did for support purposes, but the auditable record lives on the
work item itself.

### Mechanism

Every state-changing engine call (`SubmitAsync`, `CompleteTaskAsync`,
`ApplyActionAsync`, `AssignAsync`, `UnassignAsync`, `AddNoteAsync`)
automatically appends a `WorkItemAuditEntry` to `WorkItem.AuditLog` on
success. The framework owns this — modules do not opt in and cannot opt
out, so every type inherits an identical audit trail. The submission entry
is the work item's birth event: it is appended to the in-memory document
before the single `CreateAsync` call so the new document and its first
audit entry land in storage together.

### Storage

`WorkItem.AuditLog` is a `List<WorkItemAuditEntry>` persisted inline on
the work item document. An entry carries:

| Field | Purpose |
| --- | --- |
| `Id` | Server-generated GUID. |
| `Action` | Stable machine id of the action: `work-item-submitted`, `task-completed`, `action-applied`, `assigned`, `unassigned`, `note-added`. |
| `ActionDisplayName` | Human-readable description (e.g. `Task completed`). |
| `Details` | `Dictionary<string, string?>` of contextual fields per action: `typeId`/`stateId`/`templateVersion` (submission); `taskId`/`taskDisplayName`/`stateId`; `actionId`/`actionDisplayName`/`fromStateId`/`toStateId`; `assigneeId`/`assigneeName`/`previousAssigneeId`/`previousAssigneeName`; `previousAssigneeId`/`previousAssigneeName`; `noteId`. |
| `CreatedAt` | UTC timestamp from the injected `TimeProvider`. |
| `CreatedBy` | Snapshot of the actor's user id (`user:id` claim, required — mutations without it are rejected with 401). |
| `CreatedByName` | Snapshot of the actor's display name (`user:name`) at write time. |

### Wire format

`WorkItemResponse.AuditLog` is sorted **chronologically (oldest-first)** so
a UI renders a natural top-to-bottom timeline without re-sorting.

### Conventions

- **Append-only.** No edit / delete / clear endpoints — the log is the
  audit trail.
- **Failures never write.** Idempotent no-ops (e.g. completing an
  already-complete task, re-assigning to the same user, unassigning an
  already-unassigned item) and validation / authorization rejections do
  **not** append an entry. This keeps the timeline meaningful.
- **No module-specific entry shape.** If a module needs richer details,
  extend the framework's `Details` keys rather than introducing a parallel
  audit channel.
- **Snapshot identity at write time.** `CreatedBy` / `CreatedByName` are
  not live foreign keys — the audit narrative survives directory changes.

## Example: re-accreditation module (RA-98)

Reference implementation that demonstrates the framework's "one folder + one
registration line" promise. All files live under
`EprRegisterEnrolManagementBe/WorkItems/ReAccreditation/`:

| File | Role |
| --- | --- |
| `ReAccreditationType.cs` | Declares states (`submitted`, `assessment-in-progress`, `awaiting-decision`, terminal `approved` / `rejected` / `withdrawn`), per-state placeholder tasks, and transitions (`start-assessment`, `submit-for-decision`, `approve`, `reject`, `withdraw`, `withdraw-during-assessment`). |
| `Models/ReAccreditationPayload.cs` | Module's interpretation of the free-form `WorkItem.Payload` (organisation name, registration number, materials handled, previous accreditation year, compliance issues reported). |
| `IReAccreditationDecisionService.cs` / `ReAccreditationDecisionService.cs` | Module-scoped service object showing where type-specific business logic lives. Pure recommendation function (`approve` / `reject` / `more-info-needed`) over the payload. |
| `Endpoints/ReAccreditationEndpoints.cs` | Module-namespaced endpoint at `GET /work-items/re-accreditation/{id}/recommendation` — fetches the work item, deserialises the payload via `WorkItemPayloadConverter`, calls the decision service and returns `{ recommendation, rationale }`. |
| `ReAccreditationModule.cs` | Glue: exposes the type, registers the decision service in DI, mounts the endpoints. |

Wired into the application by a single line in `Program.cs.ConfigureWorkItems`:

```csharp
services.AddWorkItemModule<ReAccreditationModule>();
```

The states / tasks / transitions are placeholders for the PoC per the AC; the
intended workflow diagram is referenced in RA-85. The module inherits the
framework's audit log automatically — see `ReAccreditationLifecycleTests` for
a happy-path walk from `submitted` → `approved` that asserts every step is
captured.
