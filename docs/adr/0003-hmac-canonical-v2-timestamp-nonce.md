# ADR-0003: HMAC canonical payload v2 — timestamp + nonce required

**Date:** 2026-04-30
**Status:** Accepted

## Context

ADR-0001 established the `CognitoClientIdAuthenticationHandler` and
introduced a `Auth:SharedSecret` HMAC signature path so the backend can
verify that trust headers were assembled by the BFF rather than forged
by a caller bypassing CDP ingress. The original (`v1`) canonical payload
was:

```
v1\n{clientId}\n{userId}\n{userName}\n{roles}
```

That payload is purely a function of the trust-header values. With no
freshness component, anyone who captures one valid signed request can
replay the exact same byte stream — same headers, same signature — for
as long as the shared secret is in rotation. The signature proves
authenticity but does nothing for freshness or single-use semantics.
Issue [epr-uu3](#) flagged the gap.

The companion frontend BFF
(`lib/epr-register-enrol-management-fe`) does not currently sign at
all. There is no live HMAC code path to break, so the contract bump
described below can be made unilaterally on the backend.

## Decision

Bump the canonical signing payload to `v2` and make a fresh timestamp
and a single-use nonce mandatory whenever `Auth:SharedSecret` is set:

```
v2\n{clientId}\n{userId}\n{userName}\n{roles}\n{timestamp}\n{nonce}
```

Two new request headers (defaults):

- `x-cdp-auth-timestamp` — ISO-8601 UTC instant set by the BFF.
- `x-cdp-auth-nonce` — opaque per-request token minted by the BFF
  (e.g. base64url of 16 random bytes).

Server-side enforcement, in `CognitoClientIdAuthenticationHandler`:

- **Freshness:** the absolute difference between the supplied
  `timestamp` and the backend's `TimeProvider.GetUtcNow()` must be no
  greater than `Options.MaxClockSkew` (default 5 minutes). Enforced in
  both directions to bound replay even if a request is captured before
  the BFF clock advances.
- **Replay defence:** a successful signature verification records the
  nonce in an `IMemoryCache` with TTL `Options.ReplayCacheTtl`
  (default 10 minutes — at least `2 * MaxClockSkew`). Any subsequent
  request presenting the same nonce while it is still in the cache
  fails authentication. The cache is checked AFTER signature
  verification so a guessable-nonce attacker cannot lock legitimate
  callers out by burning their nonces with bad signatures.
- **Failure modes** each return a distinct
  `AuthenticateResult.Fail(...)` reason: missing-timestamp,
  malformed-timestamp, stale-timestamp, missing-nonce, replayed-nonce,
  invalid-signature.

The fail-CLOSED-when-unset behaviour added in epr-7vz is preserved
verbatim: a non-Development environment with no `SharedSecret` still
returns `401`; Development still falls back to header-trust mode with
the single-shot warning.

The two new headers are NOT added to the header-propagation allow-list
in `Program.cs`. They are caller-bound to THIS request: the timestamp
is bound to THIS API's clock and the nonce is burned in THIS API's
replay cache. Forwarding either downstream would either leak the
freshness/integrity proof to a service that is not the intended verifier
or replay the nonce out of band. A regression test in
`HeaderPropagationAllowListTests` enforces this.

## Consequences

### Positive

- A captured signed request can be replayed at most once and only
  within `MaxClockSkew` — at most ~5 minutes — of the BFF's clock.
- Replay attempts are observable: each one produces a `replayed-nonce`
  authentication failure.
- The new headers and the v2 payload are explicit and auditable; bumps
  are gated by the `vN` literal and so cannot be made silently.

### Negative

- Breaking change to the BFF contract. The frontend BFF must mint a
  timestamp and nonce, sign the v2 payload, and send all three new
  headers before this backend change is deployed in front of a signing
  BFF. **Today no FE code change is needed** because no live signing
  path exists in `lib/epr-register-enrol-management-fe`.
- A multi-instance deployment of the backend would need to share the
  nonce cache (e.g. Redis) for the replay defence to be meaningful at
  scale. In-memory `IMemoryCache` is acceptable for the PoC where the
  service runs as a single instance; a follow-up issue should be
  filed when scaling beyond one replica is on the roadmap.

### Neutral

- `MaxClockSkew` and `ReplayCacheTtl` are configurable via
  `CognitoClientIdAuthenticationOptions`. The defaults match this ADR
  and should not be relaxed without revisiting it.

## Verification

- `EprRegisterEnrolManagementBe.Test/Auth/CognitoClientIdAuthenticationTests.cs`
  exercises every failure mode and the happy path:
  `Signature_required_missing_timestamp_is_401`,
  `Signature_required_stale_timestamp_is_401`,
  `Signature_required_future_timestamp_is_401`,
  `Signature_required_missing_nonce_is_401`,
  `Signature_required_replayed_nonce_is_401`,
  `Signature_required_valid_signature_with_timestamp_and_nonce_is_200`.
  Tests use `FakeTimeProvider` from
  `Microsoft.Extensions.TimeProvider.Testing`.
- `EprRegisterEnrolManagementBe.Test/HeaderPropagationAllowListTests.cs`
  asserts `x-cdp-auth-timestamp` and `x-cdp-auth-nonce` are NOT on the
  propagation allow-list.
- Pre-existing `Production_environment_without_shared_secret_returns_401`
  and `Development_environment_without_shared_secret_allows_header_only_request`
  continue to pass, confirming the epr-7vz fail-closed behaviour is
  intact.

## Amendment

The configuration key was originally named `Auth:SharedSecret` (ASP.NET Core
section syntax, environment variable form `Auth__SharedSecret`). It was
subsequently renamed to `AUTH_SHARED_SECRET` to align with the CDP convention
of uppercase underscore secret names (e.g. `NOTIFY_API_KEY`). References to
`Auth:SharedSecret` in the Context and Decision sections above reflect the
original name at the time this ADR was written.
