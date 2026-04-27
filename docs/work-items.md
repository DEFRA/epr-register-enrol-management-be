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

Defined in `Backend.Api/WorkItems/Core/`:

| Type | Purpose |
| --- | --- |
| `WorkItemState` | Identifier + display name for a state. `IsTerminal` marks completion states (e.g. approved/rejected). |
| `WorkItemTask` | Identifier + display name for a unit of work to be completed in a state. |
| `IWorkItemType` | Declares a type's `TypeId`, `DisplayName`, `InitialState`, `States` and `GetTasksForState(stateId)`. Pure & side-effect free. |
| `IWorkItemModule` | A module's entry point. Exposes the `Type` and contributes `RegisterServices(services)` and `MapEndpoints(endpoints)`. |
| `IWorkItemRegistry` | DI-resolvable lookup of every registered type. |
| `WorkItem` | The persisted work item envelope: id, type id, state id, submitted-at, submitted-by (CDP Cognito client id), free-form payload. |
| `IWorkItemPersistence` | Framework-owned MongoDB persistence for `WorkItem`s. |
| `WorkItemModuleExtensions` | `AddWorkItemFramework()`, `AddWorkItemModule<T>()`, `MapWorkItemModules()`, `MapWorkItemFrameworkEndpoints()`. |

> The **task and state engine** itself (validating progressions, enforcing
> task completion before transitions, etc.) is delivered separately by RA-92
> and consumes the contracts above.

## Ingestion API

The framework exposes generic, type-agnostic endpoints for accepting and
listing work items. They live in `WorkItemEndpoints` and are mounted by
`MapWorkItemFrameworkEndpoints()`:

| Method | Route | Description |
| --- | --- | --- |
| `POST` | `/work-items` | Submit a new work item. Body: `{ "typeId": "<type>", "payload": { ... } }`. The `typeId` must be registered with the framework; the server stamps the item with the type's `InitialState`, the caller's CDP Cognito client id and a server-side timestamp. Returns `201 Created` with `Location: /work-items/{id}`. |
| `GET` | `/work-items/{id}` | Fetch a single work item by id. |
| `GET` | `/work-items` | List every persisted work item, newest first. The richer filter/search/pagination experience is delivered by RA-93. |

All three endpoints require authentication via the CDP Cognito client id
header (`x-cdp-cognito-client-id`) per RA-89/RA-85b.

The persisted envelope is owned by the framework; modules describe the shape
of their payload via their `IWorkItemType` and operate on it via their own
service objects.

## Adding a new work item type

1. **Create a folder** under `Backend.Api/WorkItems/<TypeName>/` containing:

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
  `Backend.Api/Utils`).
- Module DI registrations should use **module-scoped interfaces**
  (`IMyTypePersistence`, not `IPersistence`) to avoid colliding with other
  modules.
- A module's HTTP routes should namespace themselves under
  `/work-items/<type-id>` so they do not clash with another module's routes.
- Treat `IWorkItemType` as data: no I/O, no DI dependencies. Behaviour
  belongs in service objects registered via `RegisterServices`.
