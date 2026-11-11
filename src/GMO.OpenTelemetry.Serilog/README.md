# GMO.OpenTelemetry.Serilog

Serilog integration for `GMO.OpenTelemetry`. Targets net48, net8.0, net9.0, net10.0.

OTLP log sink with automatic chunking (messages over `MaxLogMessageLength`, default 16,000 characters, are split into chunks) and `IAttributeEnricher` enrichment.

## Logging setup

```csharp
.UseSerilog((ctx, services, logConfig) =>
    logConfig
        .ReadFrom.Configuration(ctx.Configuration)
        .AddOpenTelemetry(options.Telemetry, ctx.Configuration, services))
```

When the OTLP log pipeline is attached, it also caps `{@...}` destructuring at
`MaxSerializationDepth` levels (default 3; Serilog's own default is 10), the same limit span tags use. It
is called after `ReadFrom.Configuration`, so it replaces any `ToMaximumDepth` set there: raise
`Telemetry:MaxSerializationDepth` instead. Each level runs the logged object's getters - an EF
lazy-loading entity queries the database - and becomes log fields.

Logs are gzipped when `EnableGzipCompression` is on, which is the default: on HTTP/Protobuf through
`GzipCompressingHandler`, on gRPC through the `grpc-internal-encoding-request` header. The sink has no
compression setting of its own and ignores `OTEL_EXPORTER_OTLP_COMPRESSION`.

Over gRPC the sink's channel uses HTTP/2 keepalive pings, so a connection that dies silently is
detected within about 40 seconds and the batch is retried on a new one; without them, log export can
stop for the life of the process.

Export failures from the sink (`SelfLog`) and from the OpenTelemetry SDK are reported to standard error
and to the logger, at most once a minute per kind. The library attaches this itself, so do not call
`SelfLog.Enable` in the host: it would replace that handler.

The sink keeps every record within what New Relic stores whole:

- **255 attributes per record**, counting each field of a destructured object, and each field of a JSON
  object held in a string or making up the message, which New Relic expands. Over it New Relic drops
  the record. `Telemetry:LogsMaxAttributes` (default 240) sets the budget.
- **128 top-level attributes.** Over it New Relic discards arbitrary ones.
- **64 entries per array**, with null entries removed.
- **1 MB per export request**, rejected whole, unrelated records included. `MaxLogMessageLength`
  (default 16,000) and `LogsMaxBatchSize` (default 64) are sized to it. Chunks after the first carry
  only short context, joined by `chunk.id`, and strings longer than a chunk are shortened.

What does not fit moves to one overflow record joined by `attribute_limit.overflow_id`, with the moved
values as JSON in its message. A JSON message too large to expand gets a `JSON: ` prefix so it stays
text. Custom properties are exported under their own names only; there are no `property.*` copies.

### Without a service provider

For hosts that build their logger before the container exists:

```csharp
var loggerConfig = new LoggerConfiguration()
    .ReadFrom.Configuration(config);

// Default enricher: an AttributeEnricher wired to a CorrelationIdService, so log events
// still carry a correlation ID.
loggerConfig.AddOpenTelemetry(otelOptions, config);

// Or supply your own enricher:
loggerConfig.AddOpenTelemetryWithEnricher(otelOptions, config, myEnricher);

Log.Logger = loggerConfig.CreateLogger();
```

## Activity logging

```csharp
var logger = Log.ForContext<MyClass>().ToActivityLogger();
using var activity = new ActivityWrapper("MyOperation", activitySource, new ActivityOptions { Log = logger });
```

## License

Licensed under the GNU General Public License v3.0. See [LICENSE](../../LICENSE).
