# GMO.OpenTelemetry.SqlClient

SQL Client instrumentation for `GMO.OpenTelemetry`. Targets net48, net8.0, net9.0, and net10.0.

Adds `AddSqlClientInstrumentationWithEnrichment` which captures `db.commandTimeout`, `db.commandType`, `db.transaction.isolation_level`, and `db.params` (a JSON object keyed by parameter name, e.g. `{"@id":42}`) on each SQL span. Parameter enrichment is on net8.0, net9.0 and net10.0 — on net48, basic SQL activity tracing is registered with no enrichment.

## Usage

Call from the `additionalTracing` override:

```csharp
services.AddGmoOpenTelemetry(options.Telemetry,
    additionalTracing: tracing =>
    {
        if (options.Telemetry.EnableSqlInstrumentation)
            tracing.AddSqlClientInstrumentationWithEnrichment(
                recordException: options.Telemetry.EnableSqlInstrumentationText,
                enableTextEnrichment: options.Telemetry.EnableSqlInstrumentationText);
    });
```

Or standalone without the full bootstrap:

```csharp
tracing.AddSqlClientInstrumentationWithEnrichment(recordException: true, enableTextEnrichment: true);
```

## License

Licensed under the GNU General Public License v3.0. See [LICENSE](../../LICENSE).
