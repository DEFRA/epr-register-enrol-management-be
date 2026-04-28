# CDP deployment configuration

This document captures the metadata required to deploy
`epr-register-case-management-backend-poc` onto the CDP platform. It
complements the official
[CDP documentation](https://github.com/DEFRA/cdp-documentation) — refer to
those how-tos for the authoritative platform behaviour.

## Service identity

| Attribute      | Value                                          |
| -------------- | ---------------------------------------------- |
| Service name   | `epr-register-case-management-backend-poc`     |
| Runtime        | .NET 10 (`dotnet10`) ASP.NET Core              |
| Container port | `8085`                                         |
| Health probe   | `GET /health` (anonymous, returns `200`)       |

## Required environment variables

These are produced by the CDP portal at deploy time unless noted otherwise.

| Variable                   | Source                | Notes                                                       |
| -------------------------- | --------------------- | ----------------------------------------------------------- |
| `ASPNETCORE_URLS`          | Container             | Set to `http://+:8085` (matches `EXPOSE`).                  |
| `Mongo__DatabaseUri`       | CDP MongoDB binding   | Authenticated via IAM (`MONGODB-AWS`).                      |
| `Mongo__DatabaseName`      | Service config        | Defaults to `epr-register-case-management`.                 |
| `TraceHeader`              | Service config        | Defaults to `x-cdp-request-id`.                             |
| `HTTP_PROXY` / `HTTPS_PROXY` | CDP platform        | CDP outbound proxy. Required for any external HTTP call.    |
| `TRUSTSTORE_*`             | CDP platform          | Loaded by `LoadCustomTrustStoreFromEnvironment`.            |

## Required secrets (cdp-portal)

Create via the CDP self-service portal under the service's "secrets" tab:

- _None at PoC stage._ Secrets will be added when downstream integrations
  appear (e.g. SQS queue ARNs, third-party API keys).

## AWS resources to provision

Provision through the cdp-portal "Create a service" / "Create a resource"
flows so they appear under the service's owning team:

- ECR repository (named after the service).
- MongoDB database (`epr-register-case-management`).
- CloudWatch log group + dashboard (created automatically once the service
  publishes ECS metrics).

## Squid proxy allow-list

Outbound hostnames the service must reach from CDP environments. Add via
the cdp-portal "Outbound proxy" form:

- `cognito-idp.eu-west-2.amazonaws.com` — IAM auth for Cognito.
- `sts.eu-west-2.amazonaws.com` — STS for IAM roles for service accounts.
- `mongodb-*.eu-west-2.docdb.amazonaws.com` (CDP-managed MongoDB endpoint).
- `sqs.eu-west-2.amazonaws.com` — only when SQS queues are added.

## Related

- [docs/cdp-tracing.md](./cdp-tracing.md)
- [Registrations-353](#) — register the service in the CDP portal (prereq).
