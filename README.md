# GMO.OpenTelemetry

OpenTelemetry instrumentation (runtime, process, custom CPU %, traces, logs, metrics) for
.NET Framework 4.8 and .NET 8/9/10 services.

## Packages

| Package | Targets | Purpose |
|---------|---------|---------|
| `GMO.OpenTelemetry` | net48, net8, net9, net10 | Core bootstrap, processors, activity wrappers, metrics |
| `GMO.OpenTelemetry.Serilog` | net48, net8, net9, net10 | Serilog OTLP sink with chunking |
| `GMO.OpenTelemetry.Log4Net` | net48, net8, net9, net10 | log4net OTLP appender with chunking |
| `GMO.OpenTelemetry.SqlClient` | net48, net8, net9, net10 | SqlClient instrumentation with parameter enrichment |
| `GMO.OpenTelemetry.Npgsql` | net8, net9, net10 | Npgsql (PostgreSQL) tracing and metrics with parameter enrichment |

## Prerequisites

- **.NET 10 SDK** (reference packs for net8/9 download on restore).
- To build/run the **net48** target on Linux, install `mono-complete`; on Windows the net48 targeting
  pack is enough.

## Getting started

```bash
git clone https://github.com/gideonogegaorg/OpenTelemetry
cd OpenTelemetry
dotnet restore src/GMO.OpenTelemetry.sln
dotnet format  src/GMO.OpenTelemetry.sln   # always before building/committing
dotnet build   src/GMO.OpenTelemetry.sln -c Release
```

## Quick start

Register telemetry (modern .NET host) and instrument work with a span:

```csharp
using GMO.OpenTelemetry;
using GMO.OpenTelemetry.Serilog;

// 1. Configure from appsettings (see Configuration below), then wire up OpenTelemetry + Serilog.
builder.Services.AddOpenTelemetryWithEnricher(options);

// 2. Instrument an operation — exceptions are recorded automatically, no manual try/catch.
var logger = Log.ForContext<MyClass>().ToActivityLogger();
var result = await ActivityExecutor.ExecuteAsync("MyOperation", activitySource, async wrapper =>
{
    wrapper.SetTag("user.id", userId);
    return await DoWorkAsync();
}, new ActivityOptions { Log = logger });
```

Minimal configuration (`appsettings.json`):

```jsonc
"Telemetry": {
  "Enabled": true,
  "ServiceName": "my-service",
  "Otlp": { "Endpoint": "https://otlp.nr-data.net:4317", "Headers": "api-key=YOUR_KEY", "Protocol": "Grpc" },
  "MetricsEndpoint": "https://otlp.nr-data.net:4317",
  "TraceSourceNames": [ "my-service" ],
  "MeterSourceNames": [ "my-service" ]
}
```

> On **.NET Framework 4.8**, OTLP is always HttpProtobuf on `:4318` (no HTTP/2); `Otlp:Protocol` is
> ignored there. See [docs/configuration.md](docs/configuration.md) for the full options reference,
> endpoint/port rules, compression, and backend-specific notes.

## Testing

```bash
# Per framework (net48, net8.0, net9.0, net10.0):
dotnet test tst/GMO.OpenTelemetry.Tests/GMO.OpenTelemetry.Tests.csproj -c Release --framework net10.0
```

Full multi-target instructions, plus the TestApp smoke run that validates real OTLP export, are
in [docs/development.md](docs/development.md).

## Documentation

- [docs/architecture.md](docs/architecture.md) — design patterns and the Activity API.
- [docs/configuration.md](docs/configuration.md) — complete configuration & export-behavior reference.
- [docs/development.md](docs/development.md) — build, test, CI/CD, contributing.

## Contributing

Branch off `main`, follow `.editorconfig` (4 spaces, `_camelCase` private fields), run
`dotnet format` and the tests, then open a PR against `main`. CI (lint + build + multi-target test) must
pass. Details in [docs/development.md](docs/development.md#contributing).

## License

Licensed under the **GNU General Public License v3.0**. See [LICENSE](LICENSE).
