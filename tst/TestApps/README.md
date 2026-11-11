# OpenTelemetry TestApps

Smoke hosts that exercise `GMO.OpenTelemetry*` packages end-to-end (traces, logs, metrics) against New Relic.

## Setup (secrets)

1. Copy each host's `appsettings.json.template` to `appsettings.json` (gitignored).
2. Fill `^^OPENTELEMETRY_*^^` placeholders — mirror Telemetry from smtp-relay / GMO `Web.config`.
3. Or copy an existing local smtp-relay `appsettings.json` Telemetry block and set a unique `TraceSourceNames` / service name per host.

## Hosts

Two console apps, each multi-targeting net48, net8.0, net9.0 and net10.0. Shared wiring lives in
`GMO.OpenTelemetry.TestApps.Common` (`TestAppHost` builds the traces and metrics pipeline;
`TestAppHostRunner` auto-runs the scenarios, then listens briefly for `GET /telemetry/run`).

| Project | Logging | Listener port |
|---------|---------|---------------|
| TestApp.Serilog | Serilog OTLP sink | 5080 |
| TestApp.Log4Net | log4net OTLP appender | 5090 |

## Run

```bash
dotnet run --project tst/TestApps/TestApp.Serilog --framework net10.0
dotnet run --project tst/TestApps/TestApp.Log4Net --framework net48
```

To validate against the local collector (`docker-compose.yml` in this folder), run
`pwsh -NoProfile -File tst/TestApps/Invoke-TestAppValidation.ps1 -Build`; add `-RequireDbSpans` to
include the database scenarios. Details in
[docs/development.md](../../docs/development.md#validate-against-a-local-collector).

## Database scenarios

The scenario runner exercises real databases when a connection string is configured, and reports the
scenario as `skipped` (not a failure) when it is not:

| Setting | Client | Frameworks | Commands |
|---------|--------|------------|----------|
| `Telemetry:SqlConnectionString` | SqlClient (`System.Data.SqlClient` on net48, `Microsoft.Data.SqlClient` on net8.0+) | all | parameterised `SELECT`, then one multi-statement command (temp table, parameterised `INSERT`, `SELECT COUNT(*)`) |
| `Telemetry:PostgresConnectionString` | Npgsql, through an `NpgsqlDataSource` built with `ConfigureTracingWithEnrichment` | net8.0+ (net48 reports `skipped:unsupported-framework`) | named-parameter `SELECT`, positional-parameter `SELECT $1, $2` (array-shaped `db.params`), then an `NpgsqlBatch` of three commands |

SqlClient spans need `EnableSqlInstrumentation`; Npgsql tracing and metrics are always wired on net8.0+.
`EnableSqlInstrumentationText` turns on the enrichment tags (`db.params`, `db.commandType`,
`db.commandTimeout`, and `db.operation.batch.size` on Npgsql batches). The SqlClient enrichment
callback does not exist on .NET Framework, so net48 SqlClient spans carry only the instrumentation's own
attributes and no query text.

`docker compose -f tst/TestApps/docker-compose.yml --profile db up -d --wait` starts SQL Server Express
on `localhost,14333` and PostgreSQL on `localhost:54333` (non-default ports, so a local server on
1433 / 5432 is never hit by mistake). The throwaway credentials are in `docker-compose.yml` and
`Invoke-TestAppValidation.ps1`.

## Notes

- **Endpoints**: `Otlp:Endpoint` carries traces and points at the Infinite Tracing **trace observer**;
  `LoggingEndpoint` and `MetricsEndpoint` must stay on `https://otlp.nr-data.net`. A trace observer
  takes spans only, and `Otlp:Endpoint` is also the fallback for the other two signals, so leaving
  either unset silently discards that signal.
- **net48**: OTLP **HttpProtobuf** on `:4318` (gRPC needs HTTP/2, which Framework's `HttpClient` lacks).
- **Compression**: `EnableGzipCompression` is on in every host, as it is by default for clients. The
  log4net host builds its own logs exporter, so it calls `ApplyGzipCompression` itself.
- **Modern hosts**: gRPC on `:4317`. The library embeds `/v1/{signal}` for HttpProtobuf because
  assigning `OtlpExporterOptions.Endpoint` disables automatic path appending.
- Span ingest lags logs and metrics by a couple of minutes. A missing-span query run straight after a
  host exits is the normal way to conclude tracing is broken when it is not — re-query before digging.
- TestApps are excluded from NuGet pack and Sonar analysis.