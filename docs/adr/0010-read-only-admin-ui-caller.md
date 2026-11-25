# ADR-0010: The Admin UI is a read-only caller

**Date:** 2026-11-25
**Status:** Accepted
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
   header.
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
4. **Every read-only request is logged.** An allowed request is logged at
   Information with the caller's client id, user id, method and path, so
   upstream logs show the client and the signed-in user (an epic
   requirement). A refusal is logged at Error with the same fields: a
   read-only client asking for more than it may have is either a defect in
   that client or a stolen credential.
5. **No nation scoping.** `GET /work-items` does not read `user:role` or
   `user:nation`, so a support user sees every nation.
6. **Missing secret: reported, not fatal.** Outside Development,
   `RequiredConfigHealthCheck` lists `AUTH_SHARED_SECRET__ADMIN_UI` as missing,
   as it already does for the management-fe and backend secrets. Startup does
   not fail. A startup failure would stop the caseworker service in any
   environment that lacks a support-tool secret, including the two compose
   journey-test stacks, which run management-be outside Development and set
   only the secrets of the callers they use.

## Consequences

- A new endpoint stays closed to the Admin UI until someone opts it in, so
  the read-only guarantee does not depend on remembering to restrict each new
  route.
- The Admin UI's credentials cannot change anything, even if it signs a write
  request correctly.
- Deploy order: create `AUTH_SHARED_SECRET__ADMIN_UI` in each environment
  before deploying a version that requires it, and give the Admin UI the same
  value. Until both sides have it, the Admin UI gets `401` and `/health/ready`
  reports the missing secret.
- `/health/ready` is not the probe CDP calls (`/health` is, see
  `docs/cdp-deployment.md`). So a missing secret, for this caller or the two
  older ones, shows up only to whoever reads readiness. This ADR does not
  change that.
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
  - a pinned signature value for the eight-line form with an empty nation.
- `RequiredConfigHealthCheckTests`: the missing secret is reported outside
  Development and ignored in Development.
