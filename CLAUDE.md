# CLAUDE.md

Guidance for Claude Code (claude.ai/code) working in this repository. Deep reference lives in
[docs/](docs/) — read the relevant doc before changing behavior; this file is the map and the gotchas.

## What this is

**GMO.OpenTelemetry** — cross-targeting OpenTelemetry instrumentation for .NET Framework 4.8 and
.NET 8/9/10. Five packages: core, Serilog, Log4Net, SqlClient, Npgsql (net8.0+ only). See
[docs/architecture.md](docs/architecture.md) and [docs/configuration.md](docs/configuration.md).

## Repository layout

- `src/` — shippable libraries + the solution `src/GMO.OpenTelemetry.sln`.
- `tst/` — test projects and `tst/TestApps/` sample hosts.
- `docs/` — shared reference (architecture, configuration, development).
- Root `Directory.Build.props` (shared metadata, analyzer settings, local fallback `Version`) and
  `Directory.Packages.props` (central package versions) apply to both trees. Root `nuget.config`
  restores from nuget.org only: this repo publishes the GMO GitHub Packages feed and never consumes it.

## Essential commands

```bash
dotnet restore src/GMO.OpenTelemetry.sln
dotnet build   src/GMO.OpenTelemetry.sln -c Release
dotnet format  src/GMO.OpenTelemetry.sln --verify-no-changes   # CI lint gate — run before committing

# Tests run per framework (net48, net8.0, net9.0, net10.0):
dotnet test tst/GMO.OpenTelemetry.Tests/GMO.OpenTelemetry.Tests.csproj -c Release --framework net10.0
# Coverage for one framework (default net10.0); HTML report in coverage/report/index.html:
pwsh -NoProfile -File tst/Invoke-Coverage.ps1
# TestApp E2E validation (collector up, every app x framework, per-signal OK/MISSING table):
pwsh -NoProfile -File tst/TestApps/Invoke-TestAppValidation.ps1 -Build
```

net48 builds/runs on Linux only with `mono-complete` installed (as CI does). See
[docs/development.md](docs/development.md).

## Conventions & gotchas

- **Formatting is strictly enforced.** Run `dotnet format` before every commit or the lint job fails.
- **Multi-targeting:** guard platform-specific code with `#if NETFRAMEWORK` / `#if NET`. net48 is
  HttpProtobuf-only on `:4318`; `Otlp:Protocol` is ignored there. `GMO.OpenTelemetry.Npgsql` has no
  net48 target, so the test project references it only off net48 and its tests are `#if !NETFRAMEWORK`.
- **C# `LangVersion` is `latest` on every TFM, net48 included**; PolySharp generates internal polyfills
  (Index/Range, nullable attributes). No runtime-dependent features (default interface or static abstract
  members). net48's BCL lacks `[NotNullWhen]`, so use `Strings.IsNullOrEmpty`/`IsNullOrWhiteSpace` where
  null-state matters; `System.Threading.Lock` only under `#if NET9_0_OR_GREATER`.
- **Prefer `ActivityExecutor` / `ActivityRunner`** over hand-rolled `ActivityWrapper`: they record
  exceptions and only mark success when the delegate returns. Do **not** add your own
  `catch { wrapper.RecordException(ex); }` — that appends a duplicate `exception` event. A hand-rolled
  wrapper's `Dispose()` stamps `Ok` by default, so a thrown body reports success unless you pass
  `MarkSuccessOnDispose = false` and call `MarkSucceeded()` yourself. Set status **before** `Stop()`.
- **Adding an `ActivityOptions` property** requires updating `ActivityRunner.Merge` — the test
  `Merge_CarriesEveryActivityOptionsProperty` fails otherwise.
- **`ObjectRenderer`** depth is process-wide state; a test that changes it must use the non-parallel
  `RendererDepthState` collection. It is deliberately EF-safe (skips `IQueryable`/EF/LINQ-to-SQL
  navigations) — don't remove those guards.
- **Diagnostics tests share process-wide state.** Non-parallel collections run after parallel ones, but
  exporters and sinks left running by earlier tests keep reporting into `TelemetryDiagnostics` (and Serilog
  `SelfLog` stays enabled), so assert only on reports and windows tagged with the test's own unique source.
- **Unit tests never reach a real network target.** Point exporters/sinks at the `FakeOtlpReceiver`
  assembly fixture, give `TruncatingSpanProcessor` a stub handler, and dispose every provider, sink and
  processor a test builds. A refused `localhost` connection costs ~4 s per flush on Windows (instant on Linux
  CI), and an undisposed processor keeps posting and reporting into `TelemetryDiagnostics` after its test.
- **`SelfLog` is owned by the library** (`TelemetryDiagnostics` via `AddOpenTelemetryWithEnricher`);
  hosts must not call `SelfLog.Enable`. log4net hosts call `log.StartTelemetryDiagnostics()`.
- **Compression / chunking / New Relic limits** have subtle on-the-wire behavior — read
  [docs/configuration.md](docs/configuration.md#export-limits--chunking) before touching
  `ChunkingOpenTelemetrySink`, `LogAttributeLimiter`, `NewRelicLogLimits`, or `TruncatingSpanProcessor`.
  Verify changes to the export path with the TestApp smoke run, not just unit tests.
- **`NewRelic` is a functional backend** (an `OpenTelemetryServiceProvider` value + ingest limits), not
  branding — keep it. `OpenTelemetryServiceProvider` defaults to **`Collector`** (generic OTLP, no
  vendor endpoint validation / overflow REST), then `NewRelic`, `Dynatrace`. The library's EventSource
  is `OpenTelemetry-Gmo-Shared`.
- **Versioning is CI-driven** (`-p:Version=...`); don't hardcode per-package versions. Package versions
  are centrally managed in `Directory.Packages.props`.
- **Validating TestApps: `Telemetry__*` env vars override `appsettings.json`** (config precedence). A
  machine with `Telemetry__LoggingEndpoint`/`Telemetry__Otlp__Endpoint` set redirects every export there
  silently. `tst/TestApps/Invoke-TestAppValidation.ps1` overrides them for its child processes; see
  [docs/development.md](docs/development.md#validate-against-a-local-collector). "Auto-run success=True"
  does **not** prove export — confirm arrival at the collector.

## CI/CD

GitHub Actions (`.github/workflows/ci.yml`): lint -> build -> per-framework test -> tag -> publish to GitHub
Packages. net48 runs on the Linux runner via `mono-complete`. See
[docs/development.md](docs/development.md#cicd).

## Before opening a PR

1. `dotnet format src/GMO.OpenTelemetry.sln --verify-no-changes` passes.
2. Full unit suite green on every TFM (net48 via Mono on CI).
3. **TestApps validated** against the local collector — traces, metrics, and logs all arrive for each
   app x framework ([docs/development.md](docs/development.md#validate-against-a-local-collector)).
4. **Docs and code comments updated** to match the change.
5. **Zero new SonarCloud issues** on the PR, info level included; a passing quality gate is not enough.
   Fix before merging. List them with `curl.exe -4 -s "https://sonarcloud.io/api/issues/search?componentKeys=gideonogegaorg_OpenTelemetry&pullRequest=<N>&resolved=false"`.
   Locally, `dotnet format src/GMO.OpenTelemetry.sln --verify-no-changes --severity info` surfaces the
   Roslyn ones, but also reports naming (IDE1006) and lambda (IDE0350) suggestions Sonar does not import.
