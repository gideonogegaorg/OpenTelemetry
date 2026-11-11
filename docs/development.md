# Development

## Prerequisites

- **.NET 10 SDK** (builds all modern targets; reference packs for net8/9 download on restore).
- **net48 targeting pack** — present on Windows; on Linux install **`mono-complete`** to build and run
  the net48 target (this is how CI does it).

## Build

```bash
dotnet restore src/GMO.OpenTelemetry.sln
dotnet build   src/GMO.OpenTelemetry.sln -c Release
dotnet pack    src/GMO.OpenTelemetry.sln -c Release -o nupkgs
# Clean rebuild
dotnet clean   src/GMO.OpenTelemetry.sln && dotnet build src/GMO.OpenTelemetry.sln -c Release
```

Solution layout: shippable libraries under `src/`, tests and sample apps under `tst/`. The solution
lives at `src/GMO.OpenTelemetry.sln` and references the test projects via `..\tst\`.

## Formatting (enforced by CI)

```bash
dotnet format src/GMO.OpenTelemetry.sln                     # fix
dotnet format src/GMO.OpenTelemetry.sln --verify-no-changes # check (CI lint gate)
```

Style: 4 spaces in C#, 2 in XML/project/JSON files, `_camelCase` private fields (enforced by
`.editorconfig`). **Always run `dotnet format` before committing** — the lint job fails otherwise.

`Directory.Build.props` turns on the .NET analyzers for every project and TFM (net48 included) with
`AnalysisLevel` `latest` and `EnforceCodeStyleInBuild`, so `.editorconfig` code-style rules raised to
`warning` surface in the build. Keep the build at 0 warnings; fix a new diagnostic rather than
suppressing it.

## Testing

`tst/GMO.OpenTelemetry.Tests` multi-targets net48/net8/net9/net10. Run it per framework:

```bash
# net48 (needs Mono on Linux), net8/9/10
dotnet test tst/GMO.OpenTelemetry.Tests/GMO.OpenTelemetry.Tests.csproj -c Release --framework net48
dotnet test tst/GMO.OpenTelemetry.Tests/GMO.OpenTelemetry.Tests.csproj -c Release --framework net8.0
dotnet test tst/GMO.OpenTelemetry.Tests/GMO.OpenTelemetry.Tests.csproj -c Release --framework net9.0
dotnet test tst/GMO.OpenTelemetry.Tests/GMO.OpenTelemetry.Tests.csproj -c Release --framework net10.0
```

### Coverage

```powershell
pwsh -NoProfile -File tst/Invoke-Coverage.ps1                      # net10.0
pwsh -NoProfile -File tst/Invoke-Coverage.ps1 -Framework net8.0 -NoBuild
pwsh -NoProfile -File tst/Invoke-Coverage.ps1 -MinLineCoverage 95 -MinBranchCoverage 92   # the defaults
```

Runs the test project for one framework with the root `coverlet.runsettings` (the settings CI's
`sonar` job uses) and renders `coverage/report/index.html` (gitignored) with `reportgenerator` from the
local tool manifest `.config/dotnet-tools.json`. net48 is not offered: coverlet collects nothing under
Mono and net48 skips the `#if NET` paths.

The script enforces a coverage floor, because `coverlet.collector` has no threshold support: it prints
`OK` or `FAILED` for line coverage against `-MinLineCoverage` (default 95) and branch coverage against
`-MinBranchCoverage` (default 92). Do not lower the defaults to make a run pass; add the missing tests.
The floor is local; on CI the SonarCloud quality gate plays this role (see
[code-quality-setup.md](code-quality-setup.md#quality-gate)). Exit codes: 0 pass, 1 tests failed (the
report is still written), 2 setup or report error, 3 coverage below the floor. Its helpers are covered
by Pester 3.4 tests:
`Invoke-Pester -Script tst/Invoke-Coverage.Tests.ps1`.

### TestApp smoke validation (real OTLP export)

Unit tests never exercise real OTLP export. Two consolidated sample hosts under `tst/TestApps/` cover
the wire path (net48 HttpProtobuf, gRPC keepalive, exporter/enricher wiring); each multi-targets
net48/net8/net9/net10 and is selected with `--framework`:

- `TestApp.Serilog` — the Serilog OTLP sink path.
- `TestApp.Log4Net` — the log4net appender path.

### Validate against a local collector

`tst/TestApps/Invoke-TestAppValidation.ps1` does the whole run: starts the bundled collector
(`tst/TestApps/docker-compose.yml`, OTLP on 4317 gRPC / 4318 HTTP), points each app at it through
`Telemetry__*` overrides on the child processes, runs every app x framework, copies the collector's
`otel.json` out of the container, and prints an OK / MISSING table per service and signal. It exits 0
only when every run passed and traces, metrics, and logs arrived for every `otel-testapp-*` service.

```powershell
pwsh -NoProfile -File tst/TestApps/Invoke-TestAppValidation.ps1 -Build -RequireDbSpans
```

`-RequireDbSpans` adds the database scenarios: the script also starts SQL Server Express
(`localhost,14333`) and PostgreSQL (`localhost:54333`) from the compose `db` profile, waits for their
healthchecks (`docker compose up --wait`), passes `Telemetry__SqlConnectionString`,
`Telemetry__PostgresConnectionString`, `EnableSqlInstrumentation` and `EnableSqlInstrumentationText` to
the apps, and prints a DB spans table. It fails unless every service sent SqlClient spans and every
net8.0+ service sent Npgsql spans (Npgsql has no .NET Framework build). Spans are matched on
`db.system.name` (`microsoft.sql_server`, `postgresql`), with the older `db.system = mssql` also
accepted. The `Params` column shows whether `db.params` enrichment arrived; it stays `False` for net48
SqlClient, where the instrumentation has no enrichment callback and records no query text. Without the
switch both connection strings are blanked, so the DB scenarios report `skipped` and a machine-level
setting cannot reach a real database. What the scenarios run is in
[tst/TestApps/README.md](../tst/TestApps/README.md#database-scenarios).

Useful parameters: `-Apps Serilog,Log4Net`, `-Frameworks net48,net8.0,net9.0,net10.0`,
`-RequiredSignals traces,metrics,logs`, `-KeepCollector` (leave the containers up to inspect),
`-ExtraEnvironment @{ ... }` (extra `Telemetry__*` settings, applied last; only binds when invoked from
inside PowerShell, not through `-File`). Run logs and the copied `otel.json` go under
`%TEMP%\gmo-otel-testapp-validation`. Exit codes: 0 pass, 1 a run failed or a signal or DB span set is
missing, 2 setup error. Its parsers are covered by Pester 3.4 tests:
`Invoke-Pester -Script tst/TestApps/Invoke-TestAppValidation.Tests.ps1`.

A clean console proves nothing if a signal is missing; the collector's `debug` exporter also prints
per-batch receipts in `docker compose -f tst/TestApps/docker-compose.yml logs otel-collector`. Worth asserting beyond
arrival, by inspecting `otel.json`: span statuses (`TestApp.Root`/`NestedRoot`/`Overflow` = OK, `TestApp.Child` =
ERROR); the `db.statement` overflow log carrying `overflow = true` with `chunk.count > 1`;
`process.cpu.percent` present for every host; and `testapp.smoke.count` summing to 3 with
`testapp.smoke.duration` recording 3 values (the only check that catches a temporality regression — a
gauge looks identical under delta and cumulative).

> **Gotcha — env vars override `appsettings.json`.** `BuildConfiguration()` adds environment variables,
> which win over JSON in .NET config precedence. A machine with `Telemetry__*` user vars set (e.g.
> `Telemetry__LoggingEndpoint` -> a real New Relic endpoint) silently redirects every export there
> regardless of `appsettings.json`, so the collector stays empty. The validation script overrides them for its child processes.
> They are `Telemetry__*` (double-underscore = `:`), **not** the standard `OTEL_*` vars.
>
> Other gotchas: prefer **HTTP/4318** locally (gRPC over a Docker-published port is flaky on Windows;
> net48 is HttpProtobuf-only regardless). `dotnet run --no-build` does **not** copy `appsettings.json`
> to the output dir — rebuild or let `dotnet run` build. The bind-mounted collector file can lag on
> Windows, which is why the script reads results with `docker cp`. `docker compose down` without
> `--profile db` leaves the database containers running; the script always passes it.

## CI/CD

GitHub Actions (`.github/workflows/ci.yml`) on pushes/PRs to `main` and `dev`, and on demand
(`workflow_dispatch`). Jobs marked *DevOps* call reusable workflows in
[gideonogegaorg/DevOps](https://github.com/gideonogegaorg/DevOps) `@main`:

| Job | Runs | Does |
|-----|------|------|
| `lint` | every run | *DevOps* `dotnet-lint.yml`: `dotnet format --verify-no-changes`, and fails on any vulnerable direct or transitive NuGet package |
| `build` | after lint | installs net8/9 runtimes + `mono-complete`, restores, builds Release with a stamped version |
| `test` | after build | per-framework tests (net48 via Mono; net8/9/10 via VSTest) — the merge gate |
| `sonar` | after lint | *DevOps* `dotnet-sonarqube-scan.yml`: SonarCloud analysis with net10.0 coverage; not a dependency of tag/publish |
| `tag` | push, after test | *DevOps* `dotnet-tag.yml`: pushes tag `<branch>-YYYY-MM-<run_id>` |
| `publish` | push to `main`/`dev` | packs the shippable libraries and pushes to GitHub Packages; one run per branch at a time, never cancelled |

Separate workflows, each on pushes/PRs to `main`/`dev`, weekly and on demand: `codeql.yml` (*DevOps*
`dotnet-codeql.yml`, C# `security-extended`) and `trivy.yml` (*DevOps* `security-trivy.yml`, filesystem
vulnerability + secret scan). Dependabot updates NuGet and Actions weekly. One-time setup and
troubleshooting: [code-quality-setup.md](code-quality-setup.md).

Workflows default to `contents: read`; only `tag` (`contents: write`), `publish` (`packages: write`),
`sonar` (`pull-requests: write`) and CodeQL/Trivy (`security-events: write`) escalate. Third-party
actions are pinned by commit SHA with a `# vX.Y.Z` comment. Mono is installed by
`.github/scripts/install-mono.sh`, which the Sonar and CodeQL workflows also run as `pre_build_script`.
Lint workflows locally with `docker run --rm -v "${PWD}:/repo" -w /repo rhysd/actionlint:1.7.12` (PowerShell). From Git Bash prefix it with `MSYS_NO_PATHCONV=1`, or MSYS rewrites `-w /repo` into a Windows path and docker rejects it.

Versioning is CI-driven (`-p:Version=...`): `YYYY.MM.DD.HHMM`; `dev` packages get an `-alpha` suffix. The
local fallback version (`1.0.0`) lives in the root `Directory.Build.props` along with shared package
metadata (authors, repository URL). Package versions are centrally managed in the root
`Directory.Packages.props`.

> net48 on the Linux runners works exactly as in the upstream GitLab pipeline: install `mono-complete`
> plus the dotnet 8/9 runtimes (the SDK image ships only the net10 runtime). CI jobs run on the pinned
> `ubuntu-26.04` label, whose universe archive provides `mono-complete`; the reusable DevOps jobs pick
> their own runner.

## Contributing — pre-PR checklist

1. Branch off `main` (or `dev`); follow `.editorconfig` style (4 spaces, `_camelCase` private fields).
2. `dotnet format src/GMO.OpenTelemetry.sln --verify-no-changes` passes.
3. Full unit suite is green on **every** TFM (net48 runs under Mono on CI).
4. **TestApps validated end-to-end** against the local collector: traces, metrics, and logs (and DB
   spans, where touched) confirmed to arrive for each app x framework (see above).
5. **Documentation and code comments are up to date** with the change.
6. Open a PR against `main`; CI (lint, build, test, Sonar, CodeQL) must pass.
