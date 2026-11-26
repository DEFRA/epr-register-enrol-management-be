# ADR-0010: The Admin UI is a read-only caller

**Date:** 2026-11-25
**Status:** Accepted, amended by RA-752 (see amendment below)
**Issue:** RA-751 (epic RA-639, Production Support "Admin UI")

## Context

The Admin UI (`epr-register-enrol-admin`) is an internal production-support
tool. Its users sign in with Entra and hold the role
`Waste.SupportUser.ReadOnly`. RA-751 gives it a work item search, so it
needs to call this service.

Until now every registered caller could reach every endpoint. ADR-0005
decided that the backend authenticates the caller and otherwise trusts it
completely ("any authenticated caller can read, list, or mutate any work
item"), leaving all authorization to the frontend. ADR-0006 and ADR-0009 then
added per-caller secrets and the mgmt-tests caller under that rule:
authentication proved which caller was asking, and nothing limited what it
could do.

This ADR narrows ADR-0005 for one kind of caller. ADR-0005 was about users
and roles, which still belong in the frontend. The Admin UI is a whole service
whose credentials must never be able to write, whatever its users do, and the
only place that can be guaranteed is the service that would carry out the
write. ADR-0005 carries an amendment note pointing here. The epic requires that "credentials must only reach
read-only endpoints", and that upstream logs show the calling client and the
signed-in user.

## Decision

1. **A fourth caller with its own secret.** `AUTH_SHARED_SECRET__ADMIN_UI`
   verifies requests asserting clientId `Auth__AdminUiClientId` (default
   `epr-register-enrol-admin`). It is registered through the existing
   `AddCallerSecret` and uses the unchanged v3 HMAC contract (ADR-0003,
   ADR-0007). The Admin UI signs the eight-line form with role
   `Waste.SupportUser.ReadOnly` and an empty nation, and sends no nation
   header. It sends `x-cdp-user-name` as `encodeURIComponent(name)`, so
   `Auth:MaxUserNameLength` defaults to 2304: an Entra display name is at
   most 256 characters and encoding makes each one at most 9. The cap applies
   to every caller, because it is checked before the signature, when the
   client id is not yet proven.
2. **Read-only by client id.** `ClientIdAuthenticationOptions.ReadOnlyClientIds`
   holds the Admin UI's client id. The handler gives a request from it the
   claim `client:access = read-only`. Membership does not depend on the
   secret, so the client id stays read-only in Development header-trust mode.
3. **Deny by default.** `CallerAccessPolicies` replaces the default
   authorization policy with "authenticated and not read-only". Every endpoint
   that calls `RequireAuthorization()` with no policy, including every write
   route, refuses the Admin UI with `403`. An endpoint opts in with the
   `ReadOnlyCallerAllowed` policy, which lets other callers through unchanged
   and the read-only caller through only for GET or HEAD. Only
   `GET /work-items` and `GET /work-items/re-accreditation/states` opt in.
   The Admin UI also labels and filters add-overseas-site items with the
   re-accreditation states, which is right because both types use the
   Standard workflow's states. `AddOverseasSiteTypeTests` pins that.
4. **Every read-only request is logged.** An allowed request is logged at
   Information with the caller's client id, user id, method and path, so
   upstream logs show the client and the signed-in user (an epic
   requirement). A refusal is logged at Error with the same fields: a
   read-only client asking for more than it may have is either a defect in
   that client or a stolen credential.
5. **Projected list for read-only callers.** `GET /work-items` returns a
   read-only caller only `{ id, typeId, stateId, lastModifiedAt,
   assignedToId, assignedToName, payload: { applicationReference,
   operatorOrganisationId, organisationName } }`. This is exactly what the
   Admin UI's search shows, so the submitter, the operator's contact details
   and the rest of the payload never leave this service for that caller,
   whatever its own masking code does. Other callers get the full item.
   A projected payload field that is absent or BSON null is `null`. One
   stored as any other non-string type is corrupt data: it is also `null`
   for that item, with an Error log naming the work item id and the field
   (never the value), and the rest of the page still returns. One bad record
   must not take down the whole search, and the Admin UI shows a `null`
   field as "Missing from work item" and logs it too.
6. **Client-id collisions stop startup.** The Admin UI's client id is
   read-only whether or not its secret is set, so `Auth__AdminUiClientId`
   equal to another caller's client id would make that caller read-only. The
   service refuses to start in that case. The auth options are now resolved at
   startup (`ValidateOnStart`), which also moves the existing ADR-0006
   collision check from the first request to startup.
7. **No nation scoping.** `GET /work-items` does not read `user:role` or
   `user:nation`, so a support user sees every nation.
8. **Missing secret: Degraded, not fatal.** Outside Development, a missing
   `AUTH_SHARED_SECRET__ADMIN_UI` makes `RequiredConfigHealthCheck` report
   Degraded (HTTP 200) with an Error log, never Unhealthy. The management-fe
   and backend secrets stay Unhealthy, because without them the caseworker
   service cannot serve its own callers. Startup does not fail. A startup failure would stop the caseworker service in any
   environment that lacks a support-tool secret, including the two compose
   journey-test stacks, which run management-be outside Development and set
   only the secrets of the callers they use.

## Consequences

- A new endpoint stays closed to the Admin UI until someone opts it in, so
  the read-only guarantee does not depend on remembering to restrict each new
  route.
- The Admin UI's credentials cannot change anything, even if it signs a write
  request correctly.
- Deploy order: management-be can deploy before
  `AUTH_SHARED_SECRET__ADMIN_UI` exists. Create it, and give the Admin UI the
  same value, before deploying the Admin UI. Until both sides have it, the
  Admin UI gets `401`, `/health/ready` is Degraded and an Error is logged.
- CDP's probe is `GET /health` (`docs/cdp-deployment.md`, and the Dockerfile
  `HEALTHCHECK`). A missing Admin UI secret affects neither that probe nor,
  being only Degraded, `/health/ready`.
- RA-752 (the Work item page) will need `GET /work-items/{id}` opened to
  read-only callers. That is a one-line opt-in on the route.

## Verification

- `ClientIdAuthenticationAdminUiCallerTests`:
  - signed GETs to the two opted-in endpoints succeed with no nation filter;
  - every write route and a non-opted-in GET is refused with `403` and an
    Error log naming the client, user, method and path;
  - a wrong secret, an unconfigured secret and a missing signature give `401`;
  - the client id stays read-only in Development header-trust mode and under
    an overridden client id;
  - pinned signature values for the eight-line form with an empty nation,
    including a percent-encoded user name;
  - the admin receives only the projected fields (an absent payload field is
    null), while management-fe still receives the full item;
  - a page holding one item with a non-string payload field still returns
    `200`, with that field null and an Error log;
  - a 256-character non-ASCII name, percent-encoded to 2304 characters, is
    accepted, and one character more is refused with `401`;
  - startup fails when the Admin UI's client id equals another caller's,
    with or without the admin secret.
- `ReadOnlyWorkItemListResponseTests`: a non-string payload field (number,
  boolean, date, document, array) is null with an Error log carrying the work
  item id, field and BSON type but never the value; absent and BSON null
  fields are null without a log.
- `AddOverseasSiteTypeTests`: add-overseas-site's state ids and display names
  equal re-accreditation's.
- `RequiredConfigHealthCheckTests`: a missing Admin UI secret is Degraded
  with an Error log outside Development and ignored in Development; a missing
  caseworker caller secret stays Unhealthy.

## Amendment: RA-752 (2026-11-25)

RA-752 (the Admin UI's Work item page) opts two more GETs in to
`ReadOnlyCallerAllowed`: `GET /work-items/{id}` (Document tab and Regulator
view) and `GET /work-items/re-accreditation/{id}/prior-year` (the Regulator
view's prior-year section, which management-fe's work item page also shows).
Decision 3's list is now those two plus the RA-751 pair.

`GET /work-items/re-accreditation/{id}/recommendation` stays closed: neither
the Admin UI nor management-fe's work item page calls it, and deny by default
means a route opens only for a reader that needs it.

`GET /work-items/{id}` is **not** projected the way decision 5 projects the
list: the Admin UI gets the same full `WorkItemResponse` as management-fe,
including the payload, notes, audit log and the names they hold. RA-752 AC08
has the Document tab show the work item as JSON "including the payload, notes
and audit log, with the status pushes to the Operator journey", which a
projection would remove, and AC10 places the masking of names and email
addresses in the Admin UI when it shows them. So for this route, decision 5's
guarantee does not hold: personal data reaches the Admin UI and it is the
Admin UI's masking that keeps it off screen. Where the masking happens comes
from the ticket: AC10, and RA-752's Admin UI technical note, which extends the
Admin UI's `mask-personal-data.js` to the names work items hold. Server-side
masking for read-only callers was considered and not chosen (tech lead,
2026-11-26). A team-wide rule for personal data in the Admin UI is still to be
confirmed (beads epr-krbv).

`GET /work-items/re-accreditation/{id}/prior-year` now tells "none" from
"unavailable": `404` means there is no prior-year record, and `502`
ProblemDetails means ReEx could not answer (a non-404 failure status including
401/403, a timeout, a network failure or an unreadable body), logged at Error
with the work item id. Before RA-752 the ReEx client turned every failure into
"no record", so a caller that reads `404` as "no prior year" (the Admin UI)
could never show that the data was unavailable.

Nothing else in this ADR changes. Verification:
`ClientIdAuthenticationAdminUiCallerTests` now also asserts that the routing
table opens exactly these four GET-only endpoints to read-only callers, that
the Admin UI reads a work item (byte-identical to management-fe's) and its prior-year data, gets a plain `404`
for a missing work item or a non-GUID id, gets `404` for prior-year data
ReEx has no record of and `502` when ReEx fails, and is still refused on
writes. `HttpReExAccreditationClientTests` cover each ReEx failure.
