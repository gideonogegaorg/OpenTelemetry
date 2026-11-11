# GMO.OpenTelemetry.Log4Net

Log4Net integration for `GMO.OpenTelemetry`. Targets net48, net8.0, net9.0, net10.0.

OTLP log appender with automatic chunking (messages over `MaxLogMessageLength`, default 16,000 characters, are split into chunks) and `IAttributeEnricher` enrichment.

## Appender setup

The appender writes to an `ILoggerFactory` the host builds, so the host owns the logs exporter. Call
`ApplyGzipCompression` and `ApplyLogsBatching` there. Without the first `EnableGzipCompression` never
reaches the logs; without the second the SDK sends 512 records per request, and New Relic rejects a
request over 1 MB whole:

```csharp
using var loggerFactory = LoggerFactory.Create(builder => builder.AddOpenTelemetry(otel =>
    otel.AddOtlpExporter((exporter, processor) =>
    {
        processor.ApplyLogsBatching(options);
        exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
        exporter.Endpoint = OpenTelemetryExtensions.AppendOtlpSignalPath(endpoint, "v1/logs");
        exporter.Headers = options.GetOtlpHeaders();
        exporter.ApplyGzipCompression(options);
    })));

var appender = new OtlpLog4NetAppender(loggerFactory, attributeEnricher, options);
BasicConfigurator.Configure(appender, consoleOrFileAppender);
LogManager.GetLogger(typeof(Program)).StartTelemetryDiagnostics();
```

`StartTelemetryDiagnostics` reports OpenTelemetry SDK and exporter failures through log4net, at most
once a minute per kind, so a failing export is visible. The OTLP appender skips those reports, which
reach only the host's other appenders - configure at least one.

Chunks after the first carry only properties of up to 256 characters, joined by `chunk.id`, and a
property longer than a chunk is cut to `MaxAttributeValueLength`. A message or property that is a JSON
object, which New Relic expands into one attribute per field, gets a `JSON: ` prefix when that would
take the record past New Relic's 255-attribute limit.

## Activity logging

```csharp
var logger = LogManager.GetLogger(typeof(MyClass)).ToActivityLogger();
using var activity = new ActivityWrapper("MyOperation", activitySource, new ActivityOptions { Log = logger });
```

## License

Licensed under the GNU General Public License v3.0. See [LICENSE](../../LICENSE).
