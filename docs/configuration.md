# Configuration

Configuration is supplied through `IOpenTelemetryOptions`. See `OpenTelemetryOptionsBase` for the full
set of options and defaults.

```jsonc
"Telemetry": {
  "Enabled": true,
  "ServiceName": "my-service",
  "Otlp": {
    "Endpoint": "https://otlp.nr-data.net:4317",
    "Headers": "api-key=YOUR_LICENSE_KEY",
    "Protocol": "Grpc"
  },
  "MetricsEndpoint": "https://otlp.nr-data.net:4317",
  "TraceSourceNames": [ "my-service" ],
  "MeterSourceNames": [ "my-service" ]
}
```

## Enabling a signal without an endpoint is a startup error

With `Enabled: true`, each enabled signal needs a reachable endpoint. If none resolves, startup fails
with a message naming the keys checked — telemetry that silently goes nowhere is worse than a failed
boot. Opt a signal out instead:

| Signal  | Endpoint resolution | Opt out with |
|---------|---------------------|--------------|
| Traces  | `Otlp:Endpoint` -> `OTEL_EXPORTER_OTLP_ENDPOINT` | `TraceSourceNames: []` |
| Metrics | `MetricsEndpoint` -> the above | `MeterSourceNames: []` |
| Logs    | `LoggingEndpoint` -> the above | `EnableLogging: false` |

`Enabled: false` disables all three. `LoggingEndpoint` / `MetricsEndpoint` are per-signal overrides — a
blank one falls through to `Otlp:Endpoint` rather than suppressing the signal. `Endpoint` must be an
absolute `http`/`https` URI; a scheme-less value like `localhost:4317` parses as `Scheme=localhost` and
is discarded — write `http://localhost:4317`.

## Endpoint / header / protocol resolution

Per-signal environment variables take precedence over generic ones (as the OTLP spec requires):
**configuration -> per-signal variable -> generic variable**.

| Key | Per-signal variable | Generic variable | Default when unset |
|-----|---------------------|------------------|--------------------|
| `Otlp:Endpoint` | `OTEL_EXPORTER_OTLP_{TRACES,METRICS,LOGS}_ENDPOINT` | `OTEL_EXPORTER_OTLP_ENDPOINT` | **startup error** (if signal enabled) |
| `Otlp:Headers`  | `..._{SIGNAL}_HEADERS` | `OTEL_EXPORTER_OTLP_HEADERS` | unset |
| `Otlp:Protocol` | `..._{SIGNAL}_PROTOCOL` | `OTEL_EXPORTER_OTLP_PROTOCOL` | `Grpc` |

Blank / whitespace / malformed entries are treated as **unset**, so a stray `"Endpoint": ""` no longer
crashes the config binder. A **malformed** value — present but not an absolute `http`/`https` URI, e.g.
`localhost:4318` with no scheme — is a startup error naming the key and value (only a *blank* value is
the silent opt-out).

## Ports must match the protocol

`:4317` is gRPC; `:4318` is HTTP/protobuf. Keep `Protocol` and every endpoint consistent:

- **Modern .NET** — leave `Protocol` unset (-> `Grpc`) and use `:4317`. Use `HttpProtobuf` with `:4318`
  only when a proxy/egress rule blocks HTTP/2.
- **.NET Framework 4.8** — always HttpProtobuf on `:4318`. The library forces this and appends
  `/v1/{signal}` itself; `Otlp:Protocol` is ignored on this TFM (`HttpClient` has no HTTP/2).
- **`MetricsEndpoint` / `LoggingEndpoint`** — a per-signal endpoint on `:4318`, or carrying a
  `/v1/metrics` path, forces HttpProtobuf for that signal **regardless of `Protocol`**. So
  `https://otlp.nr-data.net:4317/v1/metrics` would post HTTP/protobuf to the gRPC port and fail — use
  `:4317` bare, or `:4318/v1/metrics`.

## Compression

`EnableGzipCompression` defaults to **true** and covers traces, metrics, and logs on every framework and
protocol. Set it `false` to disable entirely. Either way it **overrides** `OTEL_EXPORTER_OTLP_COMPRESSION`
rather than stacking on it — gzip inside gzip is undecodable by a collector.

- Exporters this library builds call `exporter.ApplyGzipCompression(options)` already. **An exporter a
  host builds itself must call it too** (e.g. the log4net appender's `ILoggerFactory`, or a hand-built
  `TracerProvider`) or the setting never reaches that signal — with no error:

  ```csharp
  .AddOtlpExporter(exporter =>
  {
      // endpoint, protocol, headers...
      exporter.ApplyGzipCompression(options);
  })
  ```
- Never compress OTLP exporters through `HttpClientFactory`: on .NET 5+ the exporter sends with
  synchronous `Send`, bypassing a handler's `SendAsync`, so nothing is compressed.
- The Serilog sink applies it itself: `GzipCompressingHandler` on HTTP/Protobuf, and the
  `grpc-internal-encoding-request=gzip` header on gRPC (a handler on gRPC would gzip the whole body and
  break framing).
- A missing or unparseable value means `true`; only a value that parses as `false` turns gzip off.
  `IConfiguration.Bind` throws at startup on an unparseable `bool`, so a host that binds its options
  should check the raw `Telemetry:EnableGzipCompression` value first and keep the `true` default when
  `bool.TryParse` fails.

## Serilog log export over gRPC

`ChunkingOpenTelemetrySink.CreateGrpcHandler` gives the sink's gRPC channel a `SocketsHttpHandler` with
HTTP/2 keepalive pings (30 s delay, 10 s timeout, `Always`). **Do not drop it.** The sink sends each
batch with no deadline and retries on the same pooled HTTP/2 connection; if a connection dies without a
reset (dropped NAT/LB path), every call times out after `HttpClient`'s 100 s and log export stops for
the life of the process while traces/metrics keep flowing. Only the failed ping discards the dead
connection. On gRPC the handler carries no compression (gzip is the request-encoding header, above).

## Infinite Tracing

`Otlp:Endpoint` is the traces endpoint **and** the fallback for logs and metrics. Pointing it at a New
Relic trace observer therefore requires setting `LoggingEndpoint` and `MetricsEndpoint` explicitly — a
trace observer takes spans only, so anything that falls through to it is silently discarded.

```jsonc
"Otlp":            { "Endpoint": "https://{trace-observer}:4317", "Protocol": "Grpc" },
"LoggingEndpoint": "https://otlp.nr-data.net",
"MetricsEndpoint": "https://otlp.nr-data.net"
```

The trace observer is documented as port 443 only but also accepts `4317` (gRPC) and `4318`
(HTTP/Protobuf, the only option on net48). A logs/metrics endpoint pointed at a host that does not serve
that signal is reported as a `Degraded` event on the `OpenTelemetry-Gmo-Shared` EventSource.

**The trace observer's own sampling defaults to keeping 1%** and nothing in this library can raise it;
set the random trace filter to 100% per observer in the New Relic UI or NerdGraph.

## Metrics temporality

The OTLP metrics exporter uses **delta** temporality: Dynatrace discards cumulative counters/histograms,
and New Relic only accepts them by converting on ingest. It is set on the metric reader, so a separately
registered exporter (e.g. Prometheus, which wants cumulative) keeps its own preference. Gauges —
including `process.cpu.percent` — are unaffected.

Framework hosts build their own `MeterProvider`, which cannot reach the DI-only `AddMetrics` /
`WithMetrics`. Use `AddGmoMetrics` so they get the same instrumentation, meters, exporter, delta, and
resource:

```csharp
_meterProvider = Sdk.CreateMeterProviderBuilder()
    .AddGmoMetrics(options)
    .Build();
```

`MetricsExportDelayMs` is the collection interval (default: the SDK's 60 s); lowering it multiplies
ingest volume rather than improving resolution of anything but gauges. `MetricsMaxQueueSize` /
`MetricsMaxBatchSize` have no effect — metrics are collected on an interval, not queued/batched.

`dotnet.thread_pool.thread.count` and `.queue.length` are declared by .NET 9+ as monotonic counters, so
under delta every fall arrives as a counter reset. `AddGmoMetricsCore` drops them
(`CustomMetrics.DropMisdeclaredRuntimeInstruments`) and `CustomMetrics` republishes them as gauges under
the same names.

## Export limits & chunking

### Chunking
Default chunk size (`MaxLogMessageLength`) is 16,000 chars, with `LogsMaxBatchSize` 64 records per
request — both sized to New Relic's **1 MB request limit**, which it rejects with a 413 for the *whole
batch* (over gRPC the 413 surfaces only as "Failed to deserialize response message"). A chunk is sent
twice (body and `message_template.text` / the log4net `message` attribute). Chunks carry `chunk.id`
(shared across one event), `chunk.index`, `chunk.count`, and an `overflow` flag; the first chunk carries
all properties, later chunks only scalar values <=256 chars. Each Serilog chunk keeps the source event's
trace/span ids (`BuildChunkEvent`). log4net hosts must call `processor.ApplyLogsBatching(options)` in
`AddOtlpExporter((exporter, processor) => ...)` (the SDK default is 512 records/request).

### New Relic log limits (handled by `LogAttributeLimiter`)
New Relic enforces these per record, silently apart from an `NrIntegrationError`:
- **255 stored attributes** (every scalar leaf of a destructured object). Over it the whole record is
  **dropped**. `LogsMaxAttributes` (default 240) budgets this.
- **128 top-level OTLP attributes** (a destructured object counts once). Over it the record is kept but
  arbitrary attributes — markers, `level` — are discarded. `TopLevelBudget` covers it.
- **JSON is expanded**: a message that is a whole JSON object, and any string attribute holding one,
  becomes one attribute per field (original string deleted). The limiter counts the fields; an
  over-budget JSON message gets a `JSON:` prefix.
- **Arrays over 64 entries** are cut and null entries removed; the full array moves to the overflow
  record.
- **Strings over the chunk size** keep `MaxAttributeValueLength` chars; the full value moves to the
  (chunked) overflow record.

The limiter trims structured properties largest first, then moves plain properties to one overflow
record joined by `attribute_limit.overflow_id`; enricher context (`level`, `trace.id`, `CorrelationId`,
`source.*`) never moves. A template-named property is frozen into literal message text before being
shortened in place. New Relic strips a leading `attributes.` from names, so markers use
`attribute_limit.*`.

### Span limits (`TruncatingSpanProcessor`, `ActivityWrapper`)
- Spans that started **>24h** before arrival are dropped; the processor reports any **>23h** old via
  `ItemsDropped` (it cannot save them).
- **Span values over 4,095 chars** are truncated by New Relic. The processor shortens string tags and
  the status description; `ActivityWrapper` shortens its own event values and status descriptions
  (`ObjectRenderer.MaxValueLength`). Instrumentation-added events (e.g. an exception stack trace) are
  immutable once added and left to New Relic.

## Breaking change — `IOpenTelemetryOptions.Otlp` is `OtlpSettings`

`Otlp` is now `OtlpSettings` rather than the SDK's `OtlpExporterOptions`, because the config binder
writes into it reflectively and that SDK type rejects the null an empty config value binds to. There is
**no implicit conversion** — assigning an `OtlpExporterOptions` no longer compiles (deliberately: its
`Endpoint` getter lazily returns `localhost:4317`, so a converted value could never mean "unset").

```csharp
// before
options.Otlp = new OtlpExporterOptions { Endpoint = uri, Headers = "api-key=k" };
// after
options.Otlp = new OtlpSettings { Endpoint = uri, Headers = "api-key=k" };
```

Services implementing `IOpenTelemetryOptions` directly, or reading `Otlp.TimeoutMilliseconds` /
`Otlp.BatchExportProcessorOptions`, must also update — per-signal batching is exposed as the
`Traces*` / `Metrics*` / `Logs*` options.
