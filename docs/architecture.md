# Architecture

## Multi-targeting

All packages target net48, net8.0, net9.0, net10.0, except `GMO.OpenTelemetry.Npgsql` (net8.0+ only:
Npgsql 10 ships no .NET Framework build). Conditional compilation bridges legacy and
modern:

- `#if NETFRAMEWORK` — `IDisposable` lifecycle, `CallContext` async-local storage, `HttpContext`,
  OTLP **HttpProtobuf only** (no HTTP/2 on Framework `HttpClient`).
- `#if NET` — `IHostedService`, `AsyncLocal<T>`, nullable reference types, implicit usings.
- The W3C Activity API is the primary trace mechanism on all targets.

## Design patterns

### Shared enrichment — `AttributeEnricher`
One instance across every logging framework. Extracts trace id, span id, correlation id, user
identity, and source location so context propagates identically regardless of the log framework.

### Framework-specific context — `CorrelationIdService`
net48 uses `CallContext.Logical{Get,Set}Data` + `HttpContext`; modern .NET uses `AsyncLocal<T>`. The
Activity API (W3C Trace Context) is primary everywhere.

### Service-provider abstraction — `OpenTelemetryServiceProvider`
Supports multiple APM vendors with different auth and endpoints:
- **New Relic** — `api-key` header; OTLP on `otlp[.region].nr-data.net` / `gov-otlp.nr-data.net`.
- **Dynatrace** — `Authorization: Api-Token` header; one base endpoint ending `/api/v2/otlp` for all
  signals. SaaS carries the tenant in the host; ActiveGate/Managed in the path (`/e/{id}/api/v2/otlp`),
  so the overflow-logs URL is derived from the configured endpoint, not rebuilt from the host label.
- Metrics export as **delta** temporality (see [configuration](configuration.md#metrics-temporality)).
- A logs/metrics endpoint on a host that does not serve that signal is reported `Degraded` on the
  `OpenTelemetry-Gmo-Shared` EventSource.

### Dual export — `TruncatingSpanProcessor`
Truncates oversized span attributes for the normal OTLP export, and asynchronously posts the full data
to the provider REST API so large traces are not lost. Also drops spans that started >23h ago (New
Relic rejects >24h) via `ItemsDropped`.

### Log chunking & limits
`ChunkingOpenTelemetrySink` (Serilog) and `OtlpLog4NetAppender` (log4net) split large log events into
chunks sized to the New Relic 1 MB request limit, with `LogAttributeLimiter` keeping each record within
New Relic's attribute/JSON-expansion limits. Full detail and the tuning knobs are in
[configuration — export limits & chunking](configuration.md#export-limits--chunking).

### Telemetry diagnostics — `TelemetryDiagnostics`
Listens to the OpenTelemetry SDK, the OTLP exporter, and this library's EventSources, and reports
warnings/errors once per minute per kind so a failing export is visible. It owns Serilog's `SelfLog`:
`AddOpenTelemetryWithEnricher` attaches it, so hosts must **not** call `SelfLog.Enable` themselves.
log4net hosts call `log.StartTelemetryDiagnostics()`. Reports carry `MarkerProperty`; both the Serilog
sink and log4net appender skip marked events (a failure report must not depend on the failing export).
Mono never dispatches EventSource events, so listener tests skip on CI net48.

## Activity API

Managed Activity/span lifecycle with automatic disposal, exception recording, and optional logging.
Prefer the executor/runner over constructing wrappers by hand.

- **`ActivityExecutor`** — runs work inside a span, taking the `ActivitySource` and `ActivityOptions`
  per call. Records exceptions automatically and calls `MarkSucceeded()` **only** when the delegate
  returns, so it never reports an unobserved success. `OperationCanceledException` adds a `cancelled`
  event and rethrows without `Error`. `ActivityOptions.ForceNewDetachedRoot` defaults to `false`
  (reuses/renames the caller's ambient root); pass `true` for consumers, background jobs, entry points.
  Methods: `Execute` / `ExecuteAsync` (child), `ExecuteRoot` / `ExecuteRootAsync` (root).
- **`ActivityRunner`** — binds an `ActivitySource` + logger **once per class** so call sites name
  neither; preferred when a type makes more than one span. `For(source, logger)` or
  `For(source, () => logger)` (per-span resolution; not memoized). `Execute*` mirror the executor;
  `Begin`/`BeginRoot` return a `using`-able wrapper for bodies that cannot be a delegate.
  `WithDefaults(options)` presets options for every span in the class. Per-call options are *merged*
  onto the binding, never stored — adding an `ActivityOptions` property requires updating
  `ActivityRunner.Merge` (a test enforces this). log4net callers bind via `log.ToActivityRunner(source)`.
- **`ActivityWrapper`** — base lifecycle. `MarkSucceeded()` sets `Ok` only if nothing else set a
  status; `Dispose()` does the same by default (`ActivityOptions.MarkSuccessOnDispose`). Mutators chain
  (`SetTag`, `SetListTags`, `AddEvent`, `RecordException`). Complex tag/event values become JSON via
  `ObjectRenderer`. Set status **before** `Stop()` — it hands the span to the SDK. There is
  deliberately no `AddTag`/`AddException` (use `SetTag` which renders, and `RecordException` which sets
  status + adds the exception event).
- **`RootActivityWrapper`** — forces detached roots for background jobs/message processing; supports
  activity links and parent-context preservation.
- **`IActivityLogger`** — logging abstraction with adapters for Microsoft.Extensions.Logging
  (`MicrosoftLoggingActivityLogger`), Serilog (`SerilogActivityLogger`), and log4net
  (`Log4NetActivityLogger`). Create via `logger.ToActivityLogger(level)`.

### `ObjectRenderer` (span-tag serialization)
Copies the object graph to at most `MaxSerializationDepth` levels (default 3) before serializing,
because an EF6 lazy-loading entity would query the database at every navigation. Anything that can run a
query is skipped: EF6/EF4/EF Core types (by base type), any `IQueryable`, and LINQ-to-SQL `[Association]`
navigations. It reads getters itself, so it honors `[JsonIgnore]` and `[JsonPropertyName]` (but not
`[JsonConverter]`). The depth is process-wide state; a test that changes it must use the non-parallel
`RendererDepthState` collection.

### Example

```csharp
using GMO.OpenTelemetry;
using GMO.OpenTelemetry.Serilog;

var logger = Log.ForContext<MyClass>().ToActivityLogger();

// Preferred: no manual try/catch — exceptions are recorded for you.
var result = await ActivityExecutor.ExecuteAsync("MyOperation", activitySource, async wrapper =>
{
    wrapper.SetTag("user.id", userId);
    return await DoWorkAsync();
}, new ActivityOptions { Log = logger });

// Root span for a consumer/background job.
await ActivityExecutor.ExecuteRootAsync("MyConsumer.Receive", activitySource,
    async wrapper => await HandleAsync(message),
    new ActivityOptions { ForceNewDetachedRoot = true, Log = logger });
```

## Component flow

```
Application
  -> IOpenTelemetryOptions (configuration)
      +- CustomMetrics (process.cpu.percent gauge) -> Metrics Provider
      +- CorrelationIdService (request tracing)    -> Activity / Baggage
      `- AttributeEnricher (shared enrichment)
           +- Serilog: OpenTelemetryEnricher + ChunkingOpenTelemetrySink
           `- log4net: OtlpLog4NetAppender
  -> TruncatingSpanProcessor (overflow -> NewRelic/Dynatrace REST for oversized data)
  -> OTLP Exporter -> APM service
```

## Critical source files

**Core** (`src/GMO.OpenTelemetry/`):
`CustomMetrics.cs` (CPU gauge; `IHostedService` on modern .NET), `CorrelationIdService.cs`,
`AttributeEnricher.cs`, `TruncatingSpanProcessor.cs` (dual export), `TelemetryDiagnostics.cs`,
`NewRelicLogLimits.cs` (JSON-expansion counting / log limits shared by both log paths),
`ObjectRenderer.cs`, `OpenTelemetryExtensions.cs` (exporter wiring + public `ApplyGzipCompression` /
`AppendOtlpSignalPath`), `GzipCompressingHandler.cs`, `ActivityExtensions.cs`, `ActivityRunner.cs`,
`ActivityExecutor.cs`, `ActivityOptions.cs`, `ActivityWrapper.cs`, `RootActivityWrapper.cs`,
`DbCommandEnricher.cs` (`db.*` enrichment tags shared by SqlClient and Npgsql; modern .NET only),
`IActivityLogger.cs`, `MicrosoftLoggingActivityLogger.cs`.

**Serilog** (`src/GMO.OpenTelemetry.Serilog/`):
`ChunkingOpenTelemetrySink.cs` (chunking + compression + gRPC keepalive + attribute limiter),
`LogAttributeLimiter.cs`, `OpenTelemetryEnricher.cs`, `SerilogTelemetryDiagnostics.cs`.

**Log4Net** (`src/GMO.OpenTelemetry.Log4Net/`):
`OtlpLog4NetAppender.cs`, `Log4NetTelemetryDiagnostics.cs` (`StartTelemetryDiagnostics()`).

**SqlClient** (`src/GMO.OpenTelemetry.SqlClient/`):
`SqlClientInstrumentationExtensions.cs` (`AddSqlClientInstrumentationWithEnrichment`).

**Npgsql** (`src/GMO.OpenTelemetry.Npgsql/`, net8.0+):
`NpgsqlInstrumentationExtensions.cs` (`AddNpgsqlInstrumentationWithEnrichment`, `AddNpgsqlMetrics`,
`ConfigureTracingWithEnrichment`). Npgsql instruments itself, so enrichment is configured on the
`NpgsqlDataSourceBuilder`, not on the `TracerProviderBuilder`.
