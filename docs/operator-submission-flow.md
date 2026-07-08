# Operator application submission flow

How a completed re-accreditation application travels from the operator's
**Submit** button through to a work item in the case management inbox.

## Participants

| Participant | Repo | Runtime |
| --- | --- | --- |
| **Operator FE** | `epr-register-enrol-frontend` | Node / Hapi |
| **Operator BE** | `epr-register-enrol-backend` | .NET |
| **Management BE** | `epr-register-enrol-management-be` (this repo) | .NET |
| **MongoDB** | Shared cluster | — |

---

## Step-by-step

### 1. User submits the declaration form (Operator FE)

The final step in the operator journey is the **Submit Declaration** page
(`/accreditation/submit-declaration/:applicationId`). The user enters their
full name, job title, and email address and clicks **Submit**.

The Hapi POST handler in `submit-declaration/controller.js` validates the
three fields. On success it calls:

```js
// src/server/accreditation/submit-declaration/controller.js:114
response = await accreditationApiService.submitApplication(
  organisationId, applicationId,
  { fullName, jobTitle, email }
)
```

`accreditationApiService.submitApplication` is a thin wrapper in
`src/server/common/helpers/accreditationApiService.js` that delegates to the
shared `apiClient`, which targets the operator BE base URL configured via
`api.baseUrl`.

---

### 2. Operator FE → Operator BE

```
POST /api/v1/accreditation-applications/{orgId}/{appId}/submit
Body: { fullName, jobTitle, email }
```

No auth headers on this leg — the FE and BE share the same CDP trust zone.

---

### 3. Validate, load and check preconditions (Operator BE)

`AccreditationApplicationEndpoints.Submit`
(`AccreditationApplication/Endpoints/AccreditationApplicationEndpoints.cs:500`)
receives the call. It:

1. Validates the three submitted-by fields.
2. Loads `AccreditationApplicationModel` from operator MongoDB.
3. Checks two guards:
   - `ApplicationStatus == Started` (already `Sent` → idempotent 200;
     any other status → 409 Conflict).
   - All three sections — PRNs, Business Plan, Sampling Plan — must be
     `Completed` (otherwise → 400).

If the guards pass the model is updated **in memory only**:
`ApplicationStatus = Sent`, `DateSent` stamped, `SubmittedBy` populated.
The DB write is deferred until after the adapter call so a management-BE
failure leaves operator MongoDB unchanged and the caller can retry.

---

### 4. Build signed HTTP request (Operator BE)

`HttpCaseWorkingApiAdapter.SubmitApplicationAsync`
(`AccreditationApplication/Adapters/HttpCaseWorkingApiAdapter.cs`) constructs
the outgoing request. The body uses a fixed `typeId` of `"re-accreditation"`
and `source` of `"operator-fe"`. The `payload` object carries all application
data (organisation name, PRNs, business plan, sampling-plan file list, etc.).

Auth headers are added by `BuildRequest`:

```
# always present
x-cdp-cognito-client-id:  epr-register-enrol-backend
x-cdp-user-id:            {submitter email}
x-cdp-user-name:          {submitter full name}

# only when CaseWorking__SharedSecret is configured
x-cdp-auth-signature:     HMAC-SHA256(secret, v2-canonical-string)
x-cdp-auth-timestamp:     2025-07-08T10:00:00Z
x-cdp-auth-nonce:         {16-byte random, base64}
```

The v2 canonical string is `v2\nclientId\nuserId\nuserName\nuserRoles\ntimestamp\nnonce`.
This is identical to the formula in this repo's `CognitoClientIdAuthenticationHandler.ComputeSignature`.
Any change to this contract is a breaking change and requires a coordinated deploy.

---

### 5. Operator BE → Management BE

```
POST http://case-management-backend:8085/work-items
```

---

### 6. Authentication (Management BE)

`CognitoClientIdAuthenticationHandler`
(`Auth/CognitoClientIdAuthenticationHandler.cs`) runs as ASP.NET middleware
before any endpoint code. When `AUTH_SHARED_SECRET` is set it enforces three
checks in order:

1. **Timestamp** — must be within ±5 minutes of server time (clock-skew
   guard against request replay).
2. **Nonce** — checked against an in-memory replay cache _after_ signature
   verification, so a guessable nonce can't burn a legitimate caller's nonce
   with a bad signature.
3. **HMAC-SHA256 signature** — compared in constant time via a double-SHA256
   wrapper (avoids timing leaks when buffer lengths differ).

On success a `ClaimsPrincipal` is created from the identity headers and the
request proceeds.

> **Fails closed**: if `AUTH_SHARED_SECRET` is not set in any non-Development
> environment the handler emits a `LogCritical` and rejects **every** request
> with 401. Trusting caller-supplied identity headers without a shared secret
> would let any service forge an identity. The fix is purely deployment config
> — see [Configuration](#configuration--the-401-matrix) below.

When a 401 fires, `HandleChallengeAsync` emits a `LogWarning` that captures
the failure reason alongside every auth-relevant header value, so operators
can diagnose mismatches without needing the caller's own logs.

---

### 7. Parse and validate (Management BE)

`WorkItemEndpoints.Submit` (`WorkItems/Core/WorkItemEndpoints.cs:101`):

1. Logs the full incoming request at `Information` level (method, path, body
   truncated to 4 096 chars, caller client ID, user ID/name, Content-Type,
   Content-Length).
2. Validates: body is a JSON object · `typeId` is present, non-empty, and
   registered in `IWorkItemRegistry` · `payload` is valid BSON · optional
   `source` is a string.

Any guard failure produces a `LogWarning` and a 400 response.

The caller-supplied `applicationReference` field (if any) is **silently
ignored** — management BE generates its own server-side so a caller can never
spoof or collide a reference.

---

### 8. Compose the work item document (Management BE)

`WorkItemService.SubmitAsync` (`WorkItems/Core/WorkItemService.cs:219`):

- Generates a server-side `applicationReference` and stamps it onto the BSON
  payload.
- Builds a `WorkItem` document:
  - `TypeId = "re-accreditation"`, `StateId = {type's initial state}`
  - `SubmittedAt / LastModifiedAt` from the injected `TimeProvider` (not
    `DateTime.UtcNow`, so `FakeTimeProvider`-based tests are reliable).
  - `TemplateSnapshot` — a frozen copy of the current template captured at
    submission time, so future template changes don't affect how past items
    render or behave.
  - `AuditLog[0]` — a `work-item-submitted` entry carrying `clientId`,
    `userId`, `source`, and `applicationReference`.
- Emits a `LogInformation` immediately before the MongoDB write:
  ```
  Persisting work item {WorkItemId}
    typeId={TypeId}  applicationReference={Ref}
    submittedBy={SubmittedBy}  (attempt {N})
  ```

---

### 9. Persist to MongoDB (Management BE → MongoDB)

`IWorkItemPersistence.CreateAsync` inserts the work item document and its
birth audit entry atomically in a single MongoDB insert. The collection has a
unique index on `applicationReference`.

**Reference collision (rare):** on a `DuplicateKey` error targeting the
`applicationReference` index the engine regenerates the reference and retries,
up to 5 attempts. Each attempt fires its own pre-persist log line. Exhaustion
surfaces as a clean 503 response (not an unhandled 500).

**Other MongoDB failures:** any exception that isn't a reference-collision
duplicate key — connection failure, timeout, unexpected write error — is caught,
logged at `Error` level with `workItemId`, `typeId`, `applicationReference`,
`submittedBy`, and the exception type name, then rethrown.

---

### 10. Log success and respond (Management BE)

After a successful insert:

```
LogInformation: Work item {WorkItemId} (re-accreditation)
                submitted in state {StateId}
                applicationReference={Ref}  by {User}
```

Post-submit hooks run (`InvokeSubmittedHooksAsync`), then the endpoint returns:

```
HTTP 201 Created
Location: /work-items/{workItemId}

{ "id": "{uuid}", "typeId": "re-accreditation",
  "stateId": "{initialState}", "payload": { ... } }
```

---

### 11. Management BE → Operator BE (response)

```
201 { id, typeId, stateId, payload }
```

---

### 12. Store result, write to operator MongoDB (Operator BE)

The adapter parses the response into a `CaseWorkingSubmissionResult`. The
endpoint stores both values on the application model and **now** writes back
to operator MongoDB:

```csharp
application.ApplicationReference      = submissionResult.ApplicationReference
application.CaseManagementReference   = submissionResult.ApplicationReference
application.CaseManagementWorkItemId  = submissionResult.WorkItemId

await persistence.UpdateAsync(application)
```

`CaseManagementWorkItemId` is used later: when the operator revisits their
application the BE calls management BE's `GET /work-items/{workItemId}` to
resolve a notification status string (e.g. _Awaiting payment_) for display.

---

### 13. Operator BE → Operator FE (response)

```
200 { accreditationReference, caseManagementReference }
```

---

### 14. Redirect to confirmation (Operator FE)

Both references are stored in the Yar session and the handler redirects to
`/accreditation/submit-confirmation/:applicationId`. The confirmation page
reads the references from session — it does not make a second API call.
If the session lacks the reference (e.g. stale direct navigation) the user
is redirected back to the task list.

The work item is now live in management BE's MongoDB and appears in the case
management FE's inbox when the work-items list is next polled.

---

## Configuration — the 401 matrix

| `CaseWorking__SharedSecret` (Operator BE) | `AUTH_SHARED_SECRET` (Management BE) | Result |
| --- | --- | --- |
| absent | absent | **401** — management BE fails closed |
| `"secret"` | absent | **401** — operator sends a signature; management BE can't verify it |
| absent | `"secret"` | **401** — no signature sent; management BE expects one |
| `"X"` | `"Y"` (different) | **401** — HMAC mismatch |
| `"secret"` | `"secret"` (matching) | **201** — work item created |

Env var names are case-insensitive in .NET's configuration system:
`CaseWorking__SharedSecret` and `CASEWORKING__SHAREDSECRET` are equivalent.
The double-underscore is the .NET convention for navigating config-section
hierarchy (`CaseWorking` section → `SharedSecret` property).

See also [ADR-0001](adr/0001-cognito-client-id-auth.md) and
[ADR-0003](adr/0003-hmac-canonical-v2-timestamp-nonce.md) for the auth design
decisions behind this contract.
