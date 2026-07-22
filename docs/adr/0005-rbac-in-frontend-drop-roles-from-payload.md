# ADR-0005: RBAC lives entirely in the frontend — drop role membership from the trust contract

**Date:** 2026-07-21
**Status:** Accepted

## Context

The architecture group (Tim Squires) has set direction that backend
services should not implement RBAC themselves — authorization belongs in
the frontend only, with backends trusting an authenticated, signed
request from the BFF. This ADR brings the case management backend in line
with that direction.

Independently of that direction, the specific checks being removed here
were already close to dead code in this service. The original design
(ADR-0001) had the backend perform some of its own authorization:
`CognitoClientIdAuthenticationHandler` turned the optional
`x-cdp-user-roles` header into `ClaimTypes.Role` claims, and the framework
used a single `case-worker` role to decide two things:

- `WorkItemEndpoints.GetAll` — whether a caller saw every work item or only
  the ones they themselves submitted (`WorkItemQuery.SubmittedBy`, inferred
  from the caller's `cognito:client_id` claim).
- `WorkItemTenancy.CanRead` — whether a caller could read or mutate a
  specific work item by id (case-worker bypass, otherwise ownership match).

In practice this authorization was dead code. The only two callers of this
backend are:

- `epr-register-enrol-management-fe` (the case-management BFF), which — by
  design (RA-323: every caseworker holds the same role) — unconditionally
  forwarded the `case-worker` role bypass on every request. Every real user
  of the case-management portal therefore always saw and could act on every
  work item; the tenancy filter never actually restricted anyone.
- `epr-register-enrol-backend` (the public applicant-facing portal), which
  never sent a roles header at all, but only ever reads/mutates work items
  it itself submitted (`SubmittedBy` already matches its own client id), so
  it always passed the ownership check anyway.

So the role-based checks added no real protection — they were a security
theatre layer sitting in front of a caller (`management-fe`) that already
had permanent access, and were irrelevant to the other caller
(`epr-register-enrol-backend`) whose access pattern never needed them.

## Decision

Authorization ("who is allowed to see or act on which work item") is
entirely the frontend/BFF's responsibility. The backend authenticates the
caller (proves the request came from a party holding the shared secret —
see ADR-0001/ADR-0003) and otherwise trusts it completely: any
authenticated caller can read, list, or mutate any work item.

Concretely:

- `WorkItemTenancy` (the `case-worker`-or-ownership gate) is deleted.
  `GetById` and every mutation endpoint (assign, unassign, notes, task
  status/completion, actions — including the ReAccreditation module's
  equivalents) no longer check who submitted the item.
- `WorkItemQuery.SubmittedBy` becomes an ordinary caller-supplied filter
  (`?submittedBy=...`) rather than a server-inferred tenancy boundary. The
  frontend can ask for a scoped or unscoped list explicitly; the backend
  applies whatever is asked for without inferring intent from role claims.
- Role membership is dropped from the trust contract entirely: the
  `x-cdp-user-roles` header, its parsing into `ClaimTypes.Role` claims, and
  the `UserRolesHeaderName`/`MaxUserRolesLength` options are removed from
  `CognitoClientIdAuthenticationHandler`.
- The HMAC canonical signing payload bumps from `v2` to `v3`, dropping the
  roles field:

  ```
  v2\n{clientId}\n{userId}\n{userName}\n{roles}\n{timestamp}\n{nonce}   (old)
  v3\n{clientId}\n{userId}\n{userName}\n{timestamp}\n{nonce}           (new)
  ```

  `userId`/`userName` are unchanged — they're still forwarded for audit
  attribution (`WorkItem.AuditLog.CreatedBy`/`CreatedByName`, note
  authorship), which is a legitimate backend concern independent of
  authorization.

## Consequences

### Positive

- Removes authorization logic that provided no actual protection and was
  easy to mistake for a real security boundary — a future contributor
  extending `WorkItemTenancy` in good faith would have been building on a
  gate the only real caller always bypassed.
- One fewer coupling point between the frontend's session/role model and
  the backend's claims model. The BFF is now free to define and evolve its
  own permission model (see `epr-register-enrol-management-fe`'s
  `auth-scopes.js`) without needing a parallel role vocabulary on the
  backend.
- Smaller trust contract: fewer header-length caps, fewer things a caller
  could get wrong when constructing a signed request.

### Negative

- **Breaking change to the signing contract.** `management-fe` and
  `epr-register-enrol-backend` (which independently ports
  `ComputeSignature` in `HttpCaseWorkingApiAdapter`) must both move to the
  `v3` payload in the same deploy as this backend change — a caller still
  signing `v2` payloads will get `401 Invalid x-cdp-auth-signature` once
  this backend verifies `v3` only. There is no dual-accept transition
  window; this must be a coordinated release across all three services.
- The backend now has no defence-in-depth against a compromised or buggy
  frontend that requests an action on a work item it shouldn't have. Given
  the frontend already had unconditional access under the old model, this
  is a change in where the boundary is documented to live, not a new
  exposure in practice — but it does mean a frontend bug is now the *only*
  thing standing between "authenticated" and "authorized." If the BFF ever
  needs to enforce per-tenant or per-role visibility restrictions, that
  logic must be built in `management-fe` — it does not exist there today
  (see `requireStandard` in `auth-scopes.js`, which only gates "is this an
  authenticated caseworker at all").

### Neutral

- `epr-register-enrol-backend`'s calls to this API were never restricted by
  the removed checks in the first place (see Context), so its behaviour is
  unaffected beyond the mandatory `v3` payload bump.

## Follow-up: per-caller shared secrets

An independent security review of this change (2026-07-22) surfaced a point
worth recording rather than acting on immediately.

Today `AUTH_SHARED_SECRET` is a single secret shared by both callers of this
backend (`management-fe` and `epr-register-enrol-backend`), and `clientId`
in the signed payload is self-asserted by the caller rather than bound to
the secret. Two consequences:

- Under the **old** (`v2`) model this meant any secret-holder could already
  forge the `case-worker` role bypass simply by adding it to the payload
  before signing — the role check never actually bounded a party that held
  the secret. This is why the Context section above is confident the old
  RBAC provided no real protection: it didn't even protect against the
  callers it was nominally guarding against, only against parties with no
  secret at all (who are stopped by authentication regardless of this
  decision).
- Under the **new** (`v3`, post-RBAC-removal) model, the practical effect is
  the same shape of exposure just without the pretence of a role gate: any
  holder of the one shared secret has full read/write over every work item.
  The secret is shared with `epr-register-enrol-backend`, a public
  applicant-facing service with materially more attack surface than the
  case-management BFF. There is currently no way to authorize or even
  distinguish "this request came from the caseworker portal" from "this
  request came from the public portal backend" — both present the same
  kind of signed payload, with `clientId` as an unverified label rather
  than a cryptographic identity.

Recommendation: move to **per-caller shared secrets** (keyed by expected
`clientId`, verified during signature check) so `clientId` becomes a
cryptographically bound identity rather than a self-asserted label. This
would not reintroduce RBAC — it is orthogonal to authorization — but it
would let a compromise of one caller (e.g. the higher-exposure public
portal) be revoked/rotated independently, and would let the backend log
and alert on a caller asserting a `clientId` it doesn't hold the matching
secret for, which is not distinguishable from a normal auth failure today.

This is not a blocker for this change — the exposure is not new (see the
first bullet above) — but should be tracked as a follow-up rather than
silently accepted. A ticket should be raised against the shared HMAC
authentication scheme (`CognitoClientIdAuthenticationHandler` and its
config in `Program.cs`/`compose/aws.env`) to scope the work.

## Verification

- `EprRegisterEnrolManagementBe.Test/Auth/CognitoClientIdAuthenticationTests.cs`
  — `ComputeSignature` call sites updated to the 6-argument `v3` form; the
  `x-cdp-user-roles` length-cap cases are removed.
- `EprRegisterEnrolManagementBe.Test/HeaderPropagationAllowListTests.cs` and
  `EprRegisterEnrolManagementBe.Test/Auth/CorsPolicyTests.cs` — `x-cdp-user-roles`
  removed from the forbidden/disallowed header lists (the header no longer
  has contract meaning).
- `EprRegisterEnrolManagementBe.Test/WorkItems/Core/WorkItemEndpointsTests.cs`,
  `WorkItemAuthBoundaryTests.cs`, and
  `EprRegisterEnrolManagementBe.Test/WorkItems/ReAccreditation/ReAccreditationEndpointTests.cs`
  — cross-tenant "returns 404" tests replaced with "succeeds regardless of
  submitter" tests; `submittedBy` filter tests updated to reflect it as an
  explicit query parameter rather than an inferred one.
- `epr-register-enrol-management-fe`'s `backend-api.test.js` and
  `sign-request.test.js` — updated to assert no `x-cdp-user-roles` header is
  sent and the `v3` canonical payload/HMAC values.
