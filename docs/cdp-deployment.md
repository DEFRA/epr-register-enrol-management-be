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
| `HTTP_PROXY` / `HTTPS_PROXY` | CDP platform        | CDP outbound proxy. Required for any external HTTP call.    |
| `TRUSTSTORE_*`             | CDP platform          | Loaded by `LoadCustomTrustStoreFromEnvironment`.            |

## Required secrets (cdp-portal)

Create via the CDP self-service portal under the service's "secrets" tab:

| Secret               | Notes                                                                                                                    |
| -------------------- | ------------------------------------------------------------------------------------------------------------------------ |
| `AUTH_SHARED_SECRET` | HMAC shared secret used by the BFF to sign trust headers (see [BFF signing contract](#bff-signing-contract) below). **Required in all non-Development environments.** The service will reject every authenticated request with `401` until this is set. Generate with `openssl rand -base64 32`. |
| `NOTIFY_API_KEY`     | GOV.UK Notify API key. When absent the service boots with a no-op Notify client — notifications are logged but not sent. |

## BFF signing contract

Every request the BFF sends to this backend must carry four headers. The
backend verifies them before accepting the CDP-injected identity headers as
authoritative. Requests missing any of these headers, or with an invalid
signature, are rejected with `401`.

| Header                    | Description                                                                                      |
| ------------------------- | ------------------------------------------------------------------------------------------------ |
| `x-cdp-cognito-client-id` | CDP-injected Cognito client ID (unchanged — CDP sets this).                                      |
| `x-cdp-auth-timestamp`    | ISO-8601 UTC instant the BFF assembled the request (e.g. `2026-05-18T10:00:00Z`). Must be within 5 minutes of the backend clock. |
| `x-cdp-auth-nonce`        | Per-request opaque random token minted by the BFF (e.g. base64url of 16 random bytes). Single-use — a replayed nonce is rejected for 10 minutes. |
| `x-cdp-auth-signature`    | Base64 HMAC-SHA256 of the canonical payload (see below) keyed with `AUTH_SHARED_SECRET`.         |

### Canonical payload (v2)

Join the following fields with a newline (`\n`), in this order, then compute
`HMAC-SHA256(key=sharedSecret, message=payload)` and base64-encode the result:

```
v2
{x-cdp-cognito-client-id}
{x-cdp-user-id or ""}
{x-cdp-user-name or ""}
{x-cdp-user-roles or ""}
{x-cdp-auth-timestamp}
{x-cdp-auth-nonce}
```

Empty-string placeholders must be included for absent optional fields — the
field count and separator positions are fixed. See
`CognitoClientIdAuthenticationHandler.ComputeSignature` for the authoritative
implementation and `docs/adr/0003-hmac-canonical-v2-timestamp-nonce.md` for
the rationale.

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
