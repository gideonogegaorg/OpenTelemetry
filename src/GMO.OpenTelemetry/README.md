# GMO.OpenTelemetry

Core OpenTelemetry bootstrap for GMO services. Targets net48, net8.0, net9.0, net10.0.

Provides: one-call bootstrap (`AddGmoOpenTelemetry`), `TruncatingSpanProcessor`, `CorrelationIdProcessor`, `AttributeEnricher`, `CorrelationIdService`, `CustomMetrics` (CPU gauge), `ActivityWrapper`/`RootActivityWrapper`, `ActivityExecutor`, `ActivityRunner`, and `IActivityLogger` adapters.

## Setup (modern .NET)

```csharp
// Worker service — wires OTLP, sampler, processors, HTTP, metrics automatically
services.AddGmoOpenTelemetry(options.Telemetry);

// Web service
services.AddGmoOpenTelemetry(options.Telemetry,
    additionalTracing: tracing => tracing.AddAspNetCoreInstrumentation(),
    additionalMetrics: metrics => metrics.AddAspNetCoreInstrumentation());

// With SQL (requires GMO.OpenTelemetry.SqlClient)
services.AddGmoOpenTelemetry(options.Telemetry,
    additionalTracing: tracing =>
    {
        tracing.AddAspNetCoreInstrumentation();
        if (options.Telemetry.EnableSqlInstrumentation)
            tracing.AddSqlClientInstrumentationWithEnrichment(
                recordException: options.Telemetry.EnableSqlInstrumentationText,
                enableTextEnrichment: options.Telemetry.EnableSqlInstrumentationText);
    });
```

`AddGmoOpenTelemetry` handles: `OtlpExporterOptions` configuration, `TruncatingSpanProcessor` + `CorrelationIdProcessor` + `IAttributeEnricher` DI registration, resource setup, `AlwaysOnSampler`, source registration, OTLP batch exporter, HTTP instrumentation (`EnableHttpInstrumentation`), console exporter (`EnableConsole`), and metrics with runtime/process/CPU gauge.

## OTLP settings

`IOpenTelemetryOptions.Otlp` is an `OtlpSettings`, not the SDK's `OtlpExporterOptions`: the configuration binder writes into it reflectively, and that type's `Endpoint` setter rejects the null an empty config value binds to — which crashed the host at startup even with `Enabled: false`, because binding is eager and total.

Resolution per signal is **configuration -> per-signal environment variable -> generic variable**, matching the OTLP spec's precedence: `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` beats `OTEL_EXPORTER_OTLP_ENDPOINT`, and likewise for `_HEADERS` and `_PROTOCOL` (protocol defaults to `Grpc`, and is always `HttpProtobuf` on net48).

A blank value means "unset" and falls through. A **malformed** one — present but not an absolute `http`/`https` URI, e.g. `localhost:4317`, which parses as `Scheme=localhost` — is a startup error naming the key and value rather than a silent fallback.

Enabling a signal with no resolvable endpoint is likewise a **startup error**: opt out with `TraceSourceNames: []`, `MeterSourceNames: []` or `EnableLogging: false`.

**Breaking change:** there is no implicit conversion from `OtlpExporterOptions` — assign an `OtlpSettings` instead. The SDK type's `Endpoint` getter lazily returns `localhost:4317`, so a converted value could never mean "unset" and would silently export to a dead collector while passing the endpoint checks. Implementing `IOpenTelemetryOptions` directly, or reading `Otlp.TimeoutMilliseconds` / `Otlp.BatchExportProcessorOptions`, also requires an update; use the `Traces*` / `Metrics*` / `Logs*` options for batching.

`TracesExportTimeoutMs` and `MetricsExportTimeoutMs` bound each export request as well as the batch, so
a slow collector cannot hold an export past them.

## Export failures

The OpenTelemetry SDK reports failed exports only through `EventSource`. Setup starts a listener that
writes warnings and errors to standard error, at most once a minute per kind, and through the logger
when the Serilog sink or log4net's `StartTelemetryDiagnostics()` is attached. It also reports any span
that started more than 23 hours before it ended: New Relic drops spans older than 24 hours.

## New Relic limits

Span values over 4,095 characters are truncated by New Relic. Long string tags and status descriptions
are shortened to `MaxAttributeValueLength`, as are the event values and status descriptions
`ActivityWrapper` writes.

On .NET 9 and later the runtime declares `dotnet.thread_pool.thread.count` and
`dotnet.thread_pool.queue.length` as monotonic counters, so under delta temporality every fall reached
New Relic as a counter reset. The metrics setup drops them and `CustomMetrics` publishes them as
gauges under the same names; a hand-built `MeterProvider` should add
`.AddView(CustomMetrics.DropMisdeclaredRuntimeInstruments)` to do the same.

A log exporter built by hand - such as the one behind the log4net appender - should call
`processor.ApplyLogsBatching(options)`: the SDK's default 512 records per request can exceed New
Relic's 1 MB request limit, which rejects the whole request.

## Compression

`EnableGzipCompression` defaults to `true` and gzips traces, metrics and logs on every framework and
protocol; set it `false` to send uncompressed. It overrides `OTEL_EXPORTER_OTLP_COMPRESSION` rather than
adding a second layer. A missing or unparseable value keeps the default.

Exporters this package builds apply it already. **An OTLP exporter you configure yourself does not** —
a hand-built `TracerProvider`, or the `ILoggerFactory` behind the log4net appender — so call the helper
inside its `AddOtlpExporter`, or that signal goes out uncompressed with no error:

```csharp
.AddOtlpExporter(exporter =>
{
    // endpoint, protocol, headers...
    exporter.ApplyGzipCompression(options);
})
```

## Span tag values

`SetTag` and `AddEvent` turn objects and collections into JSON, walking at most `MaxSerializationDepth`
levels (default 3; zero or less means the default) and at most 100 items per collection. Anything that
could query a database is never walked: Entity Framework contexts, sets, entities and lazy-loading
proxies (EF6 and EF Core, including your own `DbContext` subclasses) and any `IQueryable` are written as
their type name. A property marked `[JsonIgnore]` (System.Text.Json's or Newtonsoft's) is left out
without its getter running, and `[JsonPropertyName]` renames are honoured; `[JsonConverter]` is not
applied.

## Tracing an operation

### Bind once with `ActivityRunner`

`ActivitySource` and the logger are fixed for the lifetime of a class, but `ActivityExecutor` takes
both at every call, so `new ActivityOptions { Log = ... }` ends up repeated at every span. Bind them
once and call sites name neither:

```csharp
// The logger is resolved per span, not captured, so this works even when it is injected after
// construction or re-resolved from a container on each call.
static readonly ActivityRunner Runner = ActivityRunner.For(ActivitySource, () => Log().ToActivityLogger());

// log4net callers can bind in one step:
static readonly ActivityRunner Runner = LogManager.GetLogger(typeof(MyClass)).ToActivityRunner(ActivitySource);

Runner.Execute("MyOperation", span => DoWork());
var result = await Runner.ExecuteAsync("MyOperation", async span => await DoWorkAsync());

// Where the body cannot be a delegate - it must return, or the span outlives the block:
using (var span = Runner.Begin("MyOperation")) { ... }
```

Per-call settings still work; the runner fills in the bound logger where you do not name one, and
never stores or mutates the object you pass:

```csharp
Runner.Execute("MyOperation", span => DoWork(),
    new ActivityOptions { AdditionalData = new() { ["user.id"] = userId } });
```

When every span in a class shares a setting, bind it once instead of repeating it:

```csharp
// Sites that used to call ActivitySource.StartActivity ended Unset. Begin stamps Ok on dispose, so
// without this a failed operation would start reporting success.
static readonly ActivityRunner Runner = ActivityRunner.For(ActivitySource)
    .WithDefaults(new ActivityOptions { MarkSuccessOnDispose = false });
```

An explicit `overrides` argument replaces those defaults wholesale rather than layering onto them.

`Begin`/`BeginRoot` deliberately keep `MarkSuccessOnDispose` at its default, matching a hand-rolled
wrapper exactly so they are a safe substitution for one - which also means a `using` body that throws
without `RecordException` is still stamped `Ok`. `Execute`/`ExecuteRoot` do not have that problem.
Prefer them wherever the body can be a delegate.

### Without a bound runner


Prefer `ActivityExecutor` over constructing an `ActivityWrapper` by hand: `Dispose()` stamps an
`Unset` span as `Ok`, so an operation that throws without calling `RecordException` is reported as a
success. The executor records exceptions for you and calls `MarkSucceeded()` only when your delegate
returns, so it never reports a success it did not observe.

A hand-rolled wrapper opts into the same behaviour with `MarkSuccessOnDispose = false`, which leaves
the span `Unset` unless you call `MarkSucceeded()` yourself:

```csharp
using (var wrapper = new ActivityWrapper("MyOperation", myActivitySource,
    new ActivityOptions { MarkSuccessOnDispose = false }))
{
    DoWork();
    wrapper.MarkSucceeded();
}
```

Do not also call `RecordException` in your own `catch`: the executor already records it, and a second
call appends a duplicate `exception` event to the span.

Optional settings go in an `ActivityOptions`, used by `ActivityExecutor`, `ActivityWrapper`, and
`RootActivityWrapper` alike:

```csharp
// Child span
var result = await ActivityExecutor.ExecuteAsync(
    "MyOperation", myActivitySource,
    async activity => await DoWorkAsync());

// Root span for a consumer or entry point. ForceNewDetachedRoot clears the ambient
// context so the only parent is the remote one you pass in (or none at all).
await ActivityExecutor.ExecuteRootAsync(
    "MyConsumer.Receive", myActivitySource,
    async activity => await HandleAsync(message),
    new ActivityOptions
    {
        ForceNewDetachedRoot = true,
        ParentContext = extractedContext,
        ActivityKind = ActivityKind.Consumer,
    });
```

> **`ForceNewDetachedRoot` defaults to `false`** (matching `RootActivityWrapper`). On that default,
> when an ambient activity is present, the executor **reuses and renames the caller's existing root**
> instead of creating a span — inside an ASP.NET request that renames the request span. Set it to
> `true` for consumers, background jobs, and anything else that must own its trace.

Synchronous callers (most of the net48 codebase) use `Execute`/`ExecuteRoot` rather than blocking on
the async overloads:

```csharp
var result = ActivityExecutor.Execute(
    "MyOperation", myActivitySource,
    activity => DoWork());

ActivityExecutor.ExecuteRoot(
    "MyJob.Run", myActivitySource,
    activity => RunJob(),
    new ActivityOptions { ForceNewDetachedRoot = true });
```

Cancellation is not treated as a failure: `OperationCanceledException`/`TaskCanceledException` adds a
`cancelled` event and rethrows without setting `Error` status, so graceful shutdown does not fill the
APM tool with error spans.

## Setup (net4.8)

```csharp
var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .AddGmoInstrumentation(otelConfig)   // sampler, sources, OTLP, HTTP, console
    .AddProcessor(correlationIdProcessor)     // from DI/composition
    .AddProcessor(truncatingSpanProcessor)    // from DI/composition
    .Build();
```

## License

Licensed under the GNU General Public License v3.0. See [LICENSE](../../LICENSE).
