# CDP deployment configuration

This document captures the metadata required to deploy
`epr-register-enrol-management-be` onto the CDP platform. It
complements the official
[CDP documentation](https://github.com/DEFRA/cdp-documentation) — refer to
those how-tos for the authoritative platform behaviour.

## Service identity

| Attribute      | Value                                          |
| -------------- | ---------------------------------------------- |
| Service name   | `epr-register-enrol-management-be`     |
| Runtime        | .NET 10 (`dotnet10`) ASP.NET Core              |
| Container port | `8085`                                         |
| Health probe   | `GET /health` (anonymous, returns `200`)       |

## Required environment variables

These are produced by the CDP portal at deploy time unless noted otherwise.

| Variable                   | Source                | Notes                                                       |
| -------------------------- | --------------------- | ----------------------------------------------------------- |
| `ASPNETCORE_URLS`          | Container             | Set to `http://+:8085` (matches `EXPOSE`).                  |
| `Mongo__DatabaseUri`       | CDP MongoDB binding   | Authenticated via IAM (`MONGODB-AWS`).                      |
| `Mongo__DatabaseName`      | Service config        | Defaults to `epr-register-enrol-management-be`.     |
| `TraceHeader`              | Service config        | Defaults to `x-cdp-request-id`.                             |
| `HTTP_PROXY` / `HTTPS_PROXY` | CDP platform        | CDP outbound proxy. Required for any external HTTP call. Also assigned process-wide to `HttpClient.DefaultProxy` at startup (for the GOV.UK Notify SDK's bare `HttpClient`) — any named `HttpClient` that must bypass Squid (e.g. `"DefaultClient"`, see [Operator backend push](#operator-backend-push-ra-311mbe-1)) has to opt out explicitly via `ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseProxy = false })`; omitting a primary handler is not the same as being unproxied. |
| `TRUSTSTORE_*`             | CDP platform          | Loaded by `LoadCustomTrustStoreFromEnvironment`.            |
| `OperatorBackendApi__Enabled` | Service config     | Master switch for the RA-311/MBE-1 outbound query push (see [Operator backend push](#operator-backend-push-ra-311mbe-1) below). Defaults to `false` — deploying this code is behaviour-neutral until this is explicitly set. |
| `OperatorBackendApi__Url`  | Service config        | Internal base URL of `epr-register-enrol-backend` (CDP service-discovery name, not a public ingress hostname). Required (non-blank) when `OperatorBackendApi__Enabled=true` — startup fails otherwise. |
| `OperatorBackendApi__ClientId` | Service config    | Defaults to `epr-register-enrol-management-be`. Only override if `epr-register-enrol-backend`'s `CaseManagementAuth:ExpectedClientId` expects a different value — prefer leaving both at their defaults. |
| `Auth__ManagementFeClientId` | Service config  | The `clientId` (`x-cdp-client-id` value) that `management-fe` is expected to assert. Defaults to `frontend` — override only if `management-fe`'s own `BACKEND_API_CLIENT_ID` is set to something else. Must be distinct from `Auth__BackendClientId` (see RA-345 below). |
| `Auth__BackendClientId` | Service config      | The `clientId` that `epr-register-enrol-backend` is expected to assert. Defaults to `epr-register-enrol-backend` — override only if that service's own `CaseWorking__ClientId` is set to something else. Must be distinct from `Auth__ManagementFeClientId`; the service throws at first request if the two collide. |

## Required secrets (cdp-portal)

Create via the CDP self-service portal under the service's "secrets" tab:

| Secret               | Notes                                                                                                                    |
| -------------------- | ------------------------------------------------------------------------------------------------------------------------ |
| `AUTH_SHARED_SECRET__MANAGEMENT_FE` | HMAC secret used to verify signed trust headers from `management-fe` (see [BFF signing contract](#bff-signing-contract) below). Must match the secret `management-fe` signs with (`AUTH_SHARED_SECRET` in that service). **Required in all non-Development environments.** The service will reject every authenticated request with `401` until at least one caller's secret is set (see RA-345 below). Generate with `openssl rand -base64 32`. |
| `AUTH_SHARED_SECRET__BACKEND` | HMAC secret used to verify signed trust headers from `epr-register-enrol-backend` (see [BFF signing contract](#bff-signing-contract) below). Must match the secret `epr-register-enrol-backend` signs with (`CASE_MANAGEMENT_API_SHARED_SECRET` in that service — a flat name, not the nested `CaseWorking__*` form the rest of that service's `CaseWorking` config uses, per CDP's secrets naming convention). **Required in all non-Development environments.** Distinct from `AUTH_SHARED_SECRET__MANAGEMENT_FE` — this is the entire point of RA-345 (per-caller secrets): rotating or revoking one caller's secret must never require touching the other's. Generate with `openssl rand -base64 32`. |
| `NOTIFY_API_KEY`     | GOV.UK Notify API key. When absent the service boots with a no-op Notify client — notifications are logged but not sent. |
| `OPERATOR_BACKEND_SHARED_SECRET` | HMAC shared secret this service signs its outbound RA-311/MBE-1 query-push requests with — a flat name, not the nested `OperatorBackendApi__*` form the rest of that config section uses, per CDP's secrets naming convention. Must match `AUTH_SHARED_SECRET__MANAGEMENT_BE` on `epr-register-enrol-backend` exactly — a mismatch on either side 401s every push. The two secrets deliberately don't share a name: this service names its own outbound secrets by target/purpose (matching `epr-register-enrol-backend`'s own `CASE_MANAGEMENT_API_SHARED_SECRET` pattern for its outbound calls into this service), while `epr-register-enrol-backend` names its inbound secrets by caller (matching this service's own `AUTH_SHARED_SECRET__MANAGEMENT_FE`/`AUTH_SHARED_SECRET__BACKEND` pattern below) — same convention applied from each service's own side, not a shared literal name. **Required when `OperatorBackendApi__Enabled=true`** — startup fails otherwise. Generate with `openssl rand -base64 32`. Distinct from the two `AUTH_SHARED_SECRET__*` secrets above (this service's *inbound* secrets) and from whatever `epr-register-enrol-backend` uses for its own calls into this service (`CASE_MANAGEMENT_API_SHARED_SECRET`) — four separate secrets in total, not one, do not conflate them when rotating. |

> **RA-345 (per-caller secrets):** prior to RA-345 there was a single
> `AUTH_SHARED_SECRET` shared by both inbound callers, with `clientId`
> self-asserted rather than bound to the secret — any holder of the one
> secret could forge a request claiming to be either caller. `AUTH_SHARED_SECRET`
> is no longer read by this service; it has been replaced by the two secrets
> above, each verified only against the specific `clientId` it is registered
> for. See `docs/adr/0005-rbac-in-frontend-drop-roles-from-payload.md`'s
> "Follow-up: per-caller shared secrets" section for the rationale.

## BFF signing contract

Every request the BFF sends to this backend must carry four headers. The
backend verifies them before accepting the CDP-injected identity headers as
authoritative. Requests missing any of these headers, or with an invalid
signature, are rejected with `401`.

| Header                    | Description                                                                                      |
| ------------------------- | ------------------------------------------------------------------------------------------------ |
| `x-cdp-client-id` | The clientId the caller asserts (each caller sets this from its own config — see RA-345 above). The `x-cdp-` prefix matches this scheme's other trust headers by convention only — this value is *not* injected or verified by CDP itself, which is exactly why the HMAC signature below exists. |
| `x-cdp-auth-timestamp`    | ISO-8601 UTC instant the BFF assembled the request (e.g. `2026-05-18T10:00:00Z`). Must be within 5 minutes of the backend clock. |
| `x-cdp-auth-nonce`        | Per-request opaque random token minted by the BFF (e.g. base64url of 16 random bytes). Single-use — a replayed nonce is rejected for 10 minutes. |
| `x-cdp-auth-signature`    | Base64 HMAC-SHA256 of the canonical payload (see below), keyed with the secret registered for the asserted `x-cdp-client-id` (`AUTH_SHARED_SECRET__MANAGEMENT_FE` or `AUTH_SHARED_SECRET__BACKEND` — see RA-345 above). |

### Canonical payload (v3)

Join the following fields with a newline (`\n`), in this order, then compute
`HMAC-SHA256(key=secret for the asserted clientId, message=payload)` and base64-encode the result:

```
v3
{x-cdp-client-id}
{x-cdp-user-id or ""}
{x-cdp-user-name or ""}
{x-cdp-auth-timestamp}
{x-cdp-auth-nonce}
```

Empty-string placeholders must be included for absent optional fields — the
field count and separator positions are fixed. Role membership is not part
of the payload — authorization is entirely the BFF's concern (see
`docs/adr/0005-rbac-in-frontend-drop-roles-from-payload.md`). See
`ClientIdAuthenticationHandler.ComputeSignature` for the authoritative
implementation and `docs/adr/0003-hmac-canonical-v2-timestamp-nonce.md` for
the timestamp/nonce rationale.

## Operator backend push (RA-311/MBE-1)

When a case worker raises a query on a re-accreditation application, this
service pushes the query note and queried sections to
`epr-register-enrol-backend` so the operator's own record reflects it. The
push is off by default (`OperatorBackendApi:Enabled=false`) so deploying this
code never changes behaviour on its own — it must be explicitly turned on per
environment once `Url`/`ClientId`/`SharedSecret` are configured there. The
same flag is the rollback lever: set it back to `false` to disable the push
without a code deploy (queries still succeed for the case worker either way —
the push is fire-and-forget). Sequencing note: set the matching
`AUTH_SHARED_SECRET__MANAGEMENT_BE` on `epr-register-enrol-backend` **before**
flipping `OperatorBackendApi__Enabled=true` here, or the first pushes 401.

Every push attempt is recorded on the work item's audit log:
`query-push-sent` (2xx), `query-push-skipped` (disabled — not an error, does
not alert), or `query-push-failed` (attempted and errored — does not alert on
its own yet; see the RA-311 fix doc for the planned failure-rate alert).

The push goes out on the `"DefaultClient"` named `HttpClient`
(`Program.cs::ConfigureHttpClients`), which deliberately bypasses the Squid
proxy (`UseProxy = false`) rather than inheriting `HttpClient.DefaultProxy`.
This is required, not incidental: the target is an internal
`*.cdp-int.defra.cloud` service-discovery hostname, which isn't (and
shouldn't be) on Squid's outbound allow-list — see
[Squid proxy allow-list](#squid-proxy-allow-list). Routing this traffic
through Squid causes a `502` proxy-tunnel failure that only surfaces in CDP
(where `HTTPS_PROXY` is set), not locally.

## AWS resources to provision

Provision through the cdp-portal "Create a service" / "Create a resource"
flows so they appear under the service's owning team:

- ECR repository (named after the service).
- MongoDB database (`epr-register-enrol-management-be`).
- CloudWatch log group + dashboard (created automatically once the service
  publishes ECS metrics).

## Squid proxy allow-list

Outbound hostnames the service must reach from CDP environments. Add via
the cdp-portal "Outbound proxy" form:

- `cognito-idp.eu-west-2.amazonaws.com` — IAM auth for Cognito.
- `sts.eu-west-2.amazonaws.com` — STS for IAM roles for service accounts.
- `mongodb-*.eu-west-2.docdb.amazonaws.com` (CDP-managed MongoDB endpoint).
- `api.notifications.service.gov.uk` — GOV.UK Notify (required when `NOTIFY_API_KEY` is set).
- `sqs.eu-west-2.amazonaws.com` — only when SQS queues are added.

## Related

- [docs/cdp-tracing.md](./cdp-tracing.md)
- [Registrations-353](#) — register the service in the CDP portal (prereq).
