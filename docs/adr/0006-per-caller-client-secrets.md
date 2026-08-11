# ADR-0006: Per-caller client secrets — retire the single AUTH_SHARED_SECRET

**Date:** 2026-08-03
**Status:** Accepted
**Issue:** RA-345

## Context

`ClientIdAuthenticationHandler` verifies HMAC-signed trust headers
from this backend's two callers — `management-fe` (the internal caseworker
portal BFF) and `epr-register-enrol-backend` (the public applicant-facing
portal) — before trusting the `clientId`/`user:id`/`user:name` headers they
assert (ADR-0001, ADR-0003).

Until now, both callers were verified against a single secret,
`AUTH_SHARED_SECRET`. The `clientId` a caller asserts in the signed payload
is not itself cryptographically bound to that secret — it is just another
field in the payload, chosen by whoever is signing the request. This was
flagged as a tracked follow-up during ADR-0005 (see that ADR's "Follow-up:
per-caller shared secrets" section) and subsequently raised as its own
security ticket, RA-345, following an independent security review.

The practical consequence: any holder of the one shared secret could sign a
request asserting *either* caller's `clientId`. The two callers have
materially different risk profiles — `epr-register-enrol-backend` is
public-applicant-facing with a larger attack surface, while `management-fe`
is an internal caseworker-only portal with full read/write access to every
work item. A compromise of the lower-trust caller granted everything the
higher-trust caller could do, with no way to detect, distinguish, or
independently revoke it. This was already true before ADR-0005 removed the
in-payload `case-worker` role check (a secret-holder could already forge
that role bypass the same way), so it was not a new regression introduced
by that change — but with the role check gone, the shared secret became the
*only* thing standing between "authenticated" and "authorized" for this
backend, which raised the priority of fixing it.

## Decision

Replace the single `AUTH_SHARED_SECRET` with a secret **per known caller**,
keyed by the `clientId` that caller is expected to assert:

- `ClientIdAuthenticationOptions.SharedSecret` (`string?`) becomes
  `ClientSecrets` (`IReadOnlyDictionary<string, string>`), keyed by
  `clientId`.
- Signature verification looks up the secret registered for the `clientId`
  asserted in the request, then verifies against that specific secret —
  rather than one secret used to verify every caller regardless of the
  `clientId` it claims.
- An unrecognized `clientId`, and a `clientId` whose signature doesn't
  match the secret registered for it, both return the same externally
  visible failure (`401`, generic `"Invalid x-cdp-auth-signature header"`)
  as any other bad-signature case — this deliberately avoids creating a new
  side channel for probing which `clientId`s are known to the backend.
  Each failure path logs a distinct `LogWarning` reason internally, so the
  two cases — "no secret registered for this clientId" vs "signature
  mismatch for a known clientId" — are diagnosable in logs without being
  distinguishable over the wire.
- Config (`Program.cs::ConfigureAuth`): two new secrets,
  `AUTH_SHARED_SECRET__MANAGEMENT_FE` and `AUTH_SHARED_SECRET__BACKEND`,
  each independently rotatable. The `clientId` each is registered against
  is independently overridable via `Auth:ManagementFeClientId` /
  `Auth:BackendClientId` (defaulting to `frontend` and
  `epr-register-enrol-backend`, matching each caller's own current
  config default), mirroring the `ExpectedClientId` +
  `SharedSecret` pairing `epr-register-enrol-backend`'s
  `CaseManagementAuthConfig` already uses for the reverse-direction
  integration (`management-be` → `epr-register-enrol-backend` push).
- If the two callers' configured `clientId`s ever resolve to the same
  value (a config mistake — e.g. a copy-pasted override), the backend
  throws at options resolution rather than silently letting the
  second-registered secret overwrite the first's entry in the map. This
  surfaces as a `500` via the app's existing global exception handler,
  distinctly from a normal `401`.

No code changes were required in `management-fe` or
`epr-register-enrol-backend` — both already sign outbound requests with
their own configured secret and `clientId` (`sign-request.js`'s
`auth.sharedSecret`; `HttpCaseWorkingApiAdapter`'s
`CaseWorkingApiConfig.SharedSecret`). The only change needed on those sides
is operational: each is issued a *distinct* secret value via the CDP
self-service portal, matching the corresponding
`AUTH_SHARED_SECRET__MANAGEMENT_FE` / `AUTH_SHARED_SECRET__BACKEND` on this
service, in place of the one value both previously shared.

## Consequences

### Positive

- A compromise of one caller's secret no longer grants the other caller's
  identity — `clientId` is now cryptographically bound to a specific
  secret rather than a self-asserted label.
- Either caller's secret can be rotated or revoked independently, without
  touching the other's.
- A caller asserting a `clientId` it doesn't hold the matching secret for
  is now diagnosable in logs as a distinct failure mode, rather than being
  indistinguishable from any other signature failure.

### Negative

- Breaking change to the signing contract — the same class of change as
  the `v2`→`v3` payload bump (ADR-0005) — requiring a coordinated deploy:
  this service must have both new secrets configured before either caller
  is switched to sign with a new, different secret value, or that caller's
  requests start failing closed.
- Two secrets to provision and rotate instead of one, and a config
  misconfiguration (colliding `clientId`s) is now a distinct failure mode
  operators need to recognize (a `500`, not a `401`) — mitigated by the
  fail-loud guard in `AddCallerSecret` and by documenting it here and in
  `docs/cdp-deployment.md`.

### Neutral

- Does not reintroduce RBAC/authorization on the backend — this is purely
  about caller *authentication* identity, orthogonal to the RBAC-in-frontend
  decision in ADR-0005.
- The reverse-direction integration
  (`OperatorBackendApi`/`CaseManagementAuthenticationHandler` in
  `epr-register-enrol-backend`) already has an independent secret and only
  one caller (this service) — out of scope, unaffected.

## Verification

- `EprRegisterEnrolManagementBe.Test/Auth/ClientIdAuthenticationTests.cs`
  — existing cases updated to configure `ClientSecrets` instead of the
  single `SharedSecret`; new cases cover signing with a different known
  caller's secret while asserting another caller's `clientId` (fails),
  asserting an unrecognized `clientId` (fails), both callers authenticating
  independently with their own secrets (succeeds), and the two callers'
  `clientId`s colliding via config (throws, surfaced as `500`).
- `docs/cdp-deployment.md` and `docs/operator-submission-flow.md` updated
  to document the two new secrets and the retirement of `AUTH_SHARED_SECRET`.
