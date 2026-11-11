# GMO.OpenTelemetry — Documentation

Shared reference for the library. The root [README](../README.md) is the new-developer
quick start; [CLAUDE.md](../CLAUDE.md) is the guide for Claude Code sessions. Both link here
rather than duplicating content.

| Doc | Covers |
|-----|--------|
| [architecture.md](architecture.md) | Multi-targeting, design patterns, the Activity API (`ActivityExecutor` / `ActivityRunner` / `ActivityWrapper`), critical source files |
| [configuration.md](configuration.md) | `IOpenTelemetryOptions`, endpoint/protocol resolution, enabling signals, compression, infinite tracing, metrics temporality, export limits & chunking |
| [development.md](development.md) | Build, multi-target testing, formatting, TestApps smoke run, CI/CD, contributing |
| [code-quality-setup.md](code-quality-setup.md) | One-time SonarCloud, CodeQL, Trivy and Dependabot setup; CI quality-gate troubleshooting |

## Packages

| Package | Targets | Purpose |
|---------|---------|---------|
| `GMO.OpenTelemetry` | net48, net8, net9, net10 | Core bootstrap, processors, activity wrappers, metrics |
| `GMO.OpenTelemetry.Serilog` | net48, net8, net9, net10 | Serilog OTLP sink with chunking |
| `GMO.OpenTelemetry.Log4Net` | net48, net8, net9, net10 | log4net OTLP appender with chunking |
| `GMO.OpenTelemetry.SqlClient` | net48, net8, net9, net10 | SqlClient instrumentation with parameter enrichment |
| `GMO.OpenTelemetry.Npgsql` | net8, net9, net10 | Npgsql (PostgreSQL) tracing and metrics with parameter enrichment |

> `OpenTelemetry.Instrumentation.Process` (core metrics) is upstream **RC-only** (`1.20.1-rc.1`);
> there is no stable NuGet yet. Keep it on the newest prerelease matching the OpenTelemetry version.
