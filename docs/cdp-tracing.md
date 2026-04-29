# CDP request tracing

CDP propagates a request id via the `x-cdp-request-id` HTTP header. Every
service in the request chain must read it from inbound requests, enrich logs
with it, and forward it on outbound HTTP calls.

## Verification (Registrations-5zn)

| Concern | Status | Reference |
| --- | --- | --- |
| Header name aligns FE ↔ BE | ✅ Both use `x-cdp-request-id` | FE: [`src/config/config.js`](../../epr-register-case-management-frontend-poc/src/config/config.js) `tracing.header` default; BE: [`appsettings.json`](../EprRegisterEnrolManagementBe/appsettings.json) `TraceHeader` |
| Inbound header is propagated on outbound HttpClients | ✅ Wired via `AddHeaderPropagation` | [`Program.cs`](../EprRegisterEnrolManagementBe/Program.cs) `ConfigureHeaderPropagation` |
| Logs enriched with trace id | ✅ Serilog `Enrich.WithCorrelationId(traceIdHeader)` | [`Utils/Logging/CdpLogging.cs`](../EprRegisterEnrolManagementBe/Utils/Logging/CdpLogging.cs) |
| Outbound `HttpClient`s use `AddHttpClientWithTracing` | ⚠️ No real downstream clients yet — example call is commented out in `Program.cs::ConfigureHttpClients`. When the first real client is added it MUST use `AddHttpClientWithTracing` (or `AddHttpClientWithProxy` which composes tracing) |

## When adding a downstream HttpClient

1. Register via `services.AddHttpClientWithTracing<IFoo, FooClient>();` (see
   `EprRegisterEnrolManagementBe/Utils/Http/HttpClientExtensions.cs`).
2. Confirm the request log on the receiver shows the same `trace.id` as the
   inbound FE request.
3. If the client must traverse the CDP outbound proxy, prefer
   `AddHttpClientWithProxy` so the `ProxyHttpMessageHandler` and tracing are
   both attached.

A FE → BE → downstream smoke test should be added once any real downstream
exists; today the chain ends at the BE so no integration test is meaningful.
