# ADR-0001: Custom Cognito client_id authentication handler

**Date:** 2026-04-28
**Status:** Accepted

## Context

The backend is fronted by the CDP platform, which terminates inbound
traffic and validates the upstream service's AWS Cognito JWT before
forwarding the request. CDP injects the validated Cognito client id into
the `x-cdp-cognito-client-id` request header.

There is no standard `.NET` equivalent of `@defra/hapi-secure-context`
published by CDP. Two options were considered for backend authentication:

1. **Trust the CDP-injected header** (`x-cdp-cognito-client-id`) and rely
   on CDP to enforce JWT validation at the edge. This requires that the
   service is only reachable through CDP — direct internet exposure would
   make the header trivial to spoof.
2. **Re-validate the JWT inside the backend** using
   `Microsoft.AspNetCore.Authentication.JwtBearer` against Cognito's
   JWKS endpoint, in addition to CDP's edge validation.

## Decision

Use a hand-rolled `CognitoClientIdAuthenticationHandler` that trusts the
CDP-injected header (option 1). The handler:

- requires the `x-cdp-cognito-client-id` header to be present and
  non-empty (otherwise `401`),
- promotes the value to `ClaimTypes.NameIdentifier` and a
  `cognito:client_id` claim,
- additionally surfaces the user-id / user-name / roles forwarded by the
  BFF via `x-cdp-user-*` headers, so endpoints can do role checks and
  produce useful audit log lines without a separate user lookup.

The header is treated as authoritative because:

- the service is only reachable through the CDP ingress (network policies
  block direct exposure), and
- duplicating JWT validation would couple the PoC to Cognito-specific
  configuration (issuer, audience, JWKS URL) that has not yet been agreed
  with the platform team.

## Consequences

### Positive

- Minimal code surface; no JWT library or JWKS cache to maintain.
- Aligns with how other CDP backends consume the `x-cdp-cognito-client-id`
  contract.
- Forwarded user context (`user:id`, `user:name`, roles) is available to
  endpoints and audit logging without a second round-trip.

### Negative

- Security depends entirely on the CDP edge. If a future deployment
  exposes the service outside CDP (e.g. for direct testing) the header
  would be spoofable.
- No detection of replay or token-revocation events at the backend layer.

### Neutral

- If CDP later publishes a shared `.NET` auth library, this handler
  should be replaced with it (file a follow-up issue and supersede this
  ADR).
- A future requirement for finer-grained authz (e.g. per-route Cognito
  group checks) would need real JWT validation; this ADR will be
  revisited at that point.

## Verification

The handler is covered by `EprRegisterEnrolManagementBe.Test/Auth/` — see those tests for
the precise contract (header missing, empty, populated, with/without
forwarded user headers).
