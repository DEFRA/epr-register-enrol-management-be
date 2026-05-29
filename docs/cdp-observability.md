# Observability — operational logging

This service emits ECS-formatted JSON logs to `stdout`, which the CDP log
forwarder ships into the OpenSearch `cdp-logs*` index. See the platform
[Logging how-to](https://portal.cdp-int.defra.cloud/documentation/how-to/logging.md)
for the streamlined ECS schema that survives ingestion.

## Structured logging — `IStructuredLogger<T>`

[`Utils/Logging/StructuredLogger.cs`](../EprRegisterEnrolManagementBe/Utils/Logging/StructuredLogger.cs)
is a thin DI-friendly facade over `ILogger<T>` that lets callers
emit a log entry with a **caller-defined property bag**:

```csharp
public interface IStructuredLogger<T>
{
    void Log(
        LogLevel level,
        string message,
        IReadOnlyDictionary<string, object?> properties,
        Exception? exception = null);
}
```

The facade attaches `properties` via `ILogger.BeginScope`, so Serilog
captures every key as a structured `LogEvent` property. The generic
`T` preserves the source-context (it appears as `SourceContext` in
the ECS payload), so a single open-generic registration in
`Program.cs` is all the wiring you need:

```csharp
services.AddSingleton(typeof(IStructuredLogger<>), typeof(StructuredLogger<>));
```

Callers depend on `IStructuredLogger<TheirComponent>` and pick their
own keys — dotted ECS keys (`event.category`, `event.action`, …) map
straight onto the streamlined CDP schema; non-ECS keys (e.g.
`notify.template_key`) are kept under their own namespace and end up
in the `labels.*` ECS bucket. Tests substitute the interface with
NSubstitute and assert on the property bag directly.

## Notify-specific logging

[`GovukNotifyClient`](../EprRegisterEnrolManagementBe/Notifications/GovukNotifyClient.cs)
takes `IStructuredLogger<GovukNotifyClient>` and emits with the
following stable ECS shape on every call:

| Serilog property   | ECS field         | Notes                                                |
| ------------------ | ----------------- | ---------------------------------------------------- |
| `event.category`   | `event.category`  | Always `notify`                                      |
| `event.action`     | `event.action`    | Always `send_email`                                  |
| `event.outcome`    | `event.outcome`   | `success` \| `failure`                               |
| `event.reason`     | `event.reason`    | Short failure code (snake_case); omitted on success  |
| `event.reference`  | `event.reference` | Caller-supplied correlation id                       |
| `event.duration`   | `event.duration`  | Nanoseconds (ECS convention; `TimeSpan.Ticks * 100`) |
| `notify.template_key` | `labels.notify.template_key` | Template key, included on failures   |
| (`Exception` arg)  | `error.message` / `error.type` / `error.stack_trace` | Populated by Serilog when an exception is passed |

Retry attempts (from the Polly `OnRetry` hook) emit at `Warning`
level with `event.outcome=failure` plus `AttemptNumber` and
`RetryDelayMs` properties. The terminal failure (after retries are
exhausted) emits at `Error` level with the original exception
attached.

[`NoOpNotifyClient`](../EprRegisterEnrolManagementBe/Notifications/NoOpNotifyClient.cs)
uses the same category/action/outcome so local-dev traffic shows up
the same way in OpenSearch when the service is pointed at a CDP
environment.

`event.reason` values currently emitted by Notify:

| `event.reason`                             | Meaning                                                                |
| ------------------------------------------ | ---------------------------------------------------------------------- |
| `template_not_configured`                  | `NotifyConfig.Templates` is missing the requested template key.        |
| `send_failed_after_retries`                | All 3 SDK attempts failed; original exception attached as `error.*`.   |

The platform-injected `x-cdp-request-id` is already enriched onto every
log line as `trace.id` so
correlating an outbound failure with the originating BFF request is a
single OpenSearch filter:

```
container_name:"epr-register-enrol-management-be"
  AND event.category:"notify"
  AND event.outcome:"failure"
```

…or to pivot to a single failing request:

```
trace.id:"<x-cdp-request-id>"
```

### Template-drift contract tests

`EprRegisterEnrolManagementBe.Test/Notifications/NotifyTemplateContractTests.cs`
guards against "Notify template grew/renamed a `((placeholder))` and our
code doesn't supply it" regressions. For each `(templateKey, templateId)`
in `Notify:Templates`, the test fetches the live template body via the
Notify SDK, extracts required placeholders, and asserts they are present
in the dictionary the production `ReAccreditationNotificationHook` would
send for that path (including `approve` and `reject` separately for the
`Decision` template). Tests are tagged `[Trait("Category", "NotifyContract")]`.

- Default PR build excludes the category (`dotnet test --filter "Category!=NotifyContract"`).
- The `notify-contract` CI job runs them with `secrets.NOTIFY_API_KEY` and
  is gated on the `NOTIFY_CONTRACT_TESTS_ENABLED` repository variable so
  it only enters the required-checks surface once the dev API key secret
  exists. It also runs on a nightly schedule.
- Locally: `NOTIFY_API_KEY=… dotnet test --filter "Category=NotifyContract"`.
  Without the env var the tests skip rather than fail.

## Adding a new logged operation

1. In your component, take `IStructuredLogger<TYourComponent>` via DI.
2. Pick a stable `event.category` (the integration / subsystem name)
   and `event.action` (the verb) and **do not change them** —
   dashboards filter on these values.
3. Build a `Dictionary<string, object?>` with whatever properties
   make sense for the operation. Use dotted ECS keys (`event.*`,
   `error.*`, `http.*`, …) where the schema applies; use your own
   namespace for the rest.
4. Call `_log.Log(level, "human message", properties, exception?)`.
   Pass the exception as the last argument so Serilog populates
   `error.message` / `error.type` / `error.stack_trace`.
5. For outbound calls behind a Polly retry pipeline, log a `Warning`
   from `RetryStrategyOptions.OnRetry` and an `Error` from the
   terminal failure branch — keep the property shape consistent
   across both so the OpenSearch filter still works.

