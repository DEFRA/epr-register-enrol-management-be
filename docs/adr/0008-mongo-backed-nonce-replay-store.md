# ADR-0008: Mongo-backed nonce replay store — retire the in-memory replay cache

**Date:** 2026-09-08
**Status:** Accepted
**Issue:** RA-525

## Context

`ClientIdAuthenticationHandler` enforces single-use nonces as part of the
HMAC signature contract (ADR-0003): each signed request from `management-fe`
or `epr-register-enrol-backend` carries a nonce, and a request replaying a
previously-seen nonce within `ReplayCacheTtl` is rejected with `401`. Until
now this was tracked in an `IMemoryCache` instance registered as a singleton
in `Program.cs`.

This backend runs as multiple instances behind a load balancer in CDP (as
does every other service in this estate). An in-memory cache is scoped to a
single process: a captured signed request replayed against a *different*
instance than the one that first saw its nonce would not be recognized as a
replay at all, since each instance keeps its own independent cache. The
replay defence this handler exists to provide was therefore only ever
effective within a single instance, not across the deployment as a whole —
exactly the gap `epr-register-enrol-backend`'s own equivalent
(`CaseManagementAuthNonceStore`, epr-register-enrol-backend-0i1) was raised
and fixed for on the reverse-direction integration.

## Decision

Replace the `IMemoryCache`-backed check-then-set with a Mongo-backed
`IClientIdAuthNonceStore`, mirroring `epr-register-enrol-backend`'s
`CaseManagementAuthNonceStore` pattern 1:1:

- `ClientIdAuthNonceDocument` — one document per consumed nonce, `[BsonId]`
  `Id` is the nonce value itself, plus an `ExpiresAt` field.
- `ClientIdAuthNonceStore` (`MongoService<ClientIdAuthNonceDocument>`,
  collection `clientIdAuthNonces`) — `TryConsumeAsync` does an
  `InsertOneAsync` and returns `true` on success; a `MongoWriteException`
  with `ServerErrorCategory.DuplicateKey` (i.e. the nonce's `_id` already
  exists) is caught and returns `false` rather than throwing. The
  collection's mandatory unique index on `_id` is what makes this atomic
  across concurrent requests *and* across every running instance — no
  separate lock is needed, the same guarantee the old cache's
  `TryGetValue`/`Set` pair could never give across instances.
- A TTL index on `ExpiresAt` (`ExpireAfter = TimeSpan.Zero`) expires
  consumed-nonce documents automatically, matching the old cache's
  `AbsoluteExpirationRelativeToNow = ReplayCacheTtl` behaviour without a
  background sweep.
- `ClientIdAuthenticationHandler`'s constructor takes
  `Lazy<IClientIdAuthNonceStore> nonceStore` (not `IClientIdAuthNonceStore`
  directly — see "Lazy resolution" below) in place of `IMemoryCache
  replayCache`; the replay check
  becomes `await nonceStore.Value.TryConsumeAsync(nonce, Options.ReplayCacheTtl,
  Context.RequestAborted)`. This also made `HandleAuthenticateAsync` a
  genuinely `async` method for the first time — every other branch was
  already synchronous logic wrapped in `Task.FromResult`, now unwrapped to
  plain `return` statements.
- `Program.cs` drops `services.AddMemoryCache()` entirely (confirmed nothing
  else in this app used `IMemoryCache`) and registers
  `services.AddSingleton<IClientIdAuthNonceStore, ClientIdAuthNonceStore>()`
  in `ConfigureAuth`, alongside the scheme's other auth-scoped registrations
  — not inside `ConfigureMongo`, which only wires the generic
  `IMongoDbClientFactory` itself.
- `ReplayCacheKeyPrefix` (a cache-key namespacing constant) is removed — a
  dedicated collection needs no key namespacing, since every document in it
  is already a nonce.

### Lazy resolution — why not inject `IClientIdAuthNonceStore` directly

This service still has `app.UseAuthentication()` registered, and
`ClientIdDefaults.AuthenticationScheme` is the only registered scheme —
ASP.NET Core therefore treats it as the implicit default and constructs
`ClientIdAuthenticationHandler` for *every* inbound request, not just ones
requiring authorization (`/health`, `/health/ready`, `/swagger`, and
`/openapi/*` included; a pre-existing quirk, not introduced here).
`MongoService<T>`'s constructor eagerly calls `EnsureIndexes()`, so a plain
`IClientIdAuthNonceStore` constructor parameter would make constructing the
handler — and therefore authenticating *any* request — depend on Mongo
being reachable, even for endpoints that never carry a client-id header and
never reach the replay check. This broke `ReadinessHealthCheckTests`,
`SwaggerUiEndpointTests`, and `OpenApiEndpointTests` (all mock
`IMongoDbClientFactory` away, since they have nothing to do with Mongo) with
`500`s, and represents a genuine production regression: this service's
liveness check (`/health`, tag `"live"`) is deliberately designed to stay
green when Mongo is down, precisely so a transient Mongo outage doesn't get
an otherwise-healthy pod killed by the orchestrator. The constructor
parameter is `Lazy<IClientIdAuthNonceStore>` instead, dereferenced via
`.Value` only inside the replay-check branch — so constructing the handler
never touches Mongo, only actually verifying a signed, nonce-bearing
request does.

## Consequences

### Positive

- Nonce replay is now rejected consistently regardless of which instance
  handles the replayed request — closing the multi-instance gap described
  above.
- The insert-and-catch-duplicate-key pattern is simpler than the old
  check-then-set, and is atomic by construction rather than by careful
  ordering of two separate cache operations.

### Negative

- Adds a Mongo round-trip to every signed request's authentication path,
  where the old cache was in-process. Consistent with every other
  persistence-backed check this handler and the rest of the app already
  make; not expected to be a meaningful latency addition given the existing
  Mongo dependency.
- One more collection to account for in Mongo capacity/ops planning,
  though the TTL index keeps it self-pruning.

### Neutral

- `ReplayCacheTtl`'s name and semantics are unchanged — it still bounds how
  long a nonce is remembered as consumed — only its backing store moved
  from in-memory to Mongo.

## Verification

- `EprRegisterEnrolManagementBe.Test/Auth/ClientIdAuthenticationTests.cs` —
  existing cases (including the replay-specific
  `Signature_required_replayed_nonce_is_401`) pass unchanged against a new
  `FakeClientIdAuthNonceStore` test double (`ConcurrentDictionary`-backed),
  swapped into `BareFactory` the same way `IWorkItemPersistence` is faked
  there.
- `EprRegisterEnrolManagementBe.Test/Auth/ClientIdAuthNonceStoreTests.cs` —
  new integration tests against a real ephemeral `mongod`: first-use
  succeeds, a repeat consume of the same nonce fails, a *second store
  instance* sharing the same Mongo sees the first instance's consumed nonce
  (the actual point of this migration), a 20-way concurrent race on one
  nonce has exactly one winner, and the `ExpiresAt` TTL index is confirmed
  present with `expireAfterSeconds: 0`.
- Full solution test run: 2015 tests, 2007 succeeded, 8 skipped, 0 failed
  — including `ReadinessHealthCheckTests`, `SwaggerUiEndpointTests`, and
  `OpenApiEndpointTests`, which the direct (non-`Lazy<>`) version of this
  change broke with 500s (see "Lazy resolution" above).
