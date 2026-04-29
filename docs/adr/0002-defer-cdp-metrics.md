# ADR-0002: Defer CDP metrics & auditing for the backend PoC

**Date:** 2026-04-28
**Status:** Accepted

## Context

The companion frontend PoC consumes `@defra/cdp-metrics` and
`@defra/cdp-auditing` (Node packages) to publish CloudWatch EMF metrics
and structured audit-log events that the CDP platform's dashboards and
alerting depend on.

The backend PoC currently emits Serilog logs in Elastic Common Schema
format (via `Elastic.Serilog.*`) but has no equivalent metrics or
audit-log output. CDP does not yet publish a first-party `.NET` package
analogous to `@defra/cdp-metrics`; comparable functionality would be
provided by either:

- the AWS EMF .NET SDK (`Amazon.CloudWatch.EMF`), or
- an OpenTelemetry pipeline exporting to CloudWatch via the ADOT
  collector.

## Decision

Defer adding metrics and audit-log emission from the backend until the
PoC is promoted past the proof-of-concept stage. The decision will be
revisited when:

- the first deployable end-to-end user journey lands in `dev`, **or**
- CDP publishes an officially-supported `.NET` metrics package.

Until then, the existing Serilog ECS output is sufficient for the platform
to ingest logs and produce ad-hoc CloudWatch Logs Insights queries.

## Consequences

### Positive

- Avoids committing to a metrics SDK choice (EMF SDK vs. OpenTelemetry)
  while the platform's preferred approach is still in flux.
- Keeps the backend PoC free of premature platform-specific dependencies.

### Negative

- The backend will not appear in the CDP-provided metrics dashboards
  until this ADR is superseded.
- Alerting on the backend in deployed environments will rely on log-based
  alarms rather than EMF metric alarms.

### Neutral

- A follow-up issue should be opened when either trigger condition above
  is met. The replacement ADR should specify the chosen library and the
  minimum metric set (request count, latency p95, error rate).

## Verification

`EprRegisterEnrolManagementBe.csproj` does not reference any metrics package. Logs are
verified by the Serilog `CdpLogging` configuration and its tests.
