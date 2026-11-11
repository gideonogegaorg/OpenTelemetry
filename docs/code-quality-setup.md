# Code quality setup

One-time configuration and troubleshooting for the quality and security workflows. The reusable
workflows they call live in [gideonogegaorg/DevOps](https://github.com/gideonogegaorg/DevOps)
(`.github/workflows/`); the jobs themselves are described in
[development.md](development.md#cicd).

## Prerequisite: DevOps hook inputs

The `sonar` job in `ci.yml` passes `pre_build_script` and `test_framework`, and `codeql.yml` passes
`pre_build_script`. Both are needed because the solution multi-targets net48:

- `pre_build_script: .github/scripts/install-mono.sh` installs `mono-complete` so net48 builds on the
  Linux runner.
- `test_framework: net10.0` runs coverage on one TFM. Coverlet collects nothing under Mono, and with
  `--no-build` every test project in the solution must target the chosen TFM.

These inputs must exist in DevOps `main` (`dotnet-sonarqube-scan.yml`, `dotnet-codeql.yml`). Until
they do, both workflows fail at startup with `Invalid input, pre_build_script is not defined in the
referenced workflow`.

## SonarCloud

Project key **`gideonogegaorg_OpenTelemetry`**, organization **`gideonogegaorg`**, SonarCloud main
branch **`main`** (matches the GitHub default branch; `dev` is analyzed as a long-lived branch).

1. Import the repository at
   [sonarcloud.io/organizations/gideonogegaorg/projects](https://sonarcloud.io/organizations/gideonogegaorg/projects),
   or run the DevOps `sonarcloud-provision-project.yml` workflow.
2. **Administration → Analysis Method:** turn **Automatic Analysis off**. C# coverage is only
   imported by CI-based analysis, and SonarCloud rejects a CI scan while Automatic Analysis is on.
3. **Administration → Pull Requests / GitHub integration:** enable **PR decoration**. The calling job
   grants `pull-requests: write` for this.
4. **Administration → Branches:** confirm `main` is the main branch and that
   *Detection of long-lived branches* matches `main|dev`. On pushes to `main` the workflow renames the
   SonarCloud main branch to `main` if it differs.
5. **Token:** use the org secret `SONAR_TOKEN` on **gideonogegaorg** (Settings → Secrets and variables
   → Actions). Delete any repository-level `SONAR_TOKEN` on this repo: a repo secret shadows the org
   secret and a stale one breaks analysis silently. The token user must be an org member with
   **Execute Analysis** and **Browse**, plus **Administer Project** on `gideonogegaorg_OpenTelemetry`
   for the main-branch rename.

Scanner settings (exclusions, OpenCover paths, quality gate wait) are passed on
`dotnet sonarscanner begin` by the reusable workflow. Do **not** add `sonar-project.properties`; the
.NET scanner rejects it. Analysis excludes `tst/**` (tests and TestApps), `bin`, `obj`, `coverage`,
`nupkgs` and `.github`. Coverage uses the root [`coverlet.runsettings`](../coverlet.runsettings)
(OpenCover; test and TestApp assemblies excluded).

PRs from forks get no secrets: the scanner steps are skipped and only build and tests run.

### Quality gate

The gate is configured in the SonarCloud UI (**Quality Gates**, assigned under the project's
**Administration → Quality Gate**), not in this repo or the workflow. Recommended coverage conditions:

- **Coverage on New Code >= 90%.**
- **Coverage (overall) >= 95%**, SonarCloud's combined line and branch figure.

Do not gate at 98% or above. The remaining uncovered code is mostly defensive `catch` blocks that only
a broken host reaches, branches the compiler adds when lowering `?.` and `??`, and net48-only code that
coverage cannot measure (coverlet collects nothing under Mono), so a higher gate would demand tests that
prove nothing. Locally, `tst/Invoke-Coverage.ps1` enforces its own floor (line >= 95%, branch >= 92%;
see [development.md](development.md#coverage)).

### Common failures

| Symptom | Fix |
|---------|-----|
| `Invalid input, pre_build_script is not defined` / `test_framework is not defined` | Merge the DevOps hook inputs to DevOps `main` (see above) |
| `SONAR_TOKEN is empty` | Org secret missing, or the run is from a fork |
| `SonarCloud project ... was not found` | Import the project (step 1); check the key is `gideonogegaorg_OpenTelemetry` |
| `You are running CI analysis while Automatic Analysis is enabled` | Turn Automatic Analysis off (step 2) |
| `Could not rename SonarCloud main branch` | Grant the token user **Administer Project**, or set `main` as main branch manually |
| Coverage 0% | The test step ran without `test_framework` (Mono yields no coverage), or `coverlet.runsettings` was moved |
| Quality gate failed | Fix the code or add tests; do not loosen the gate |

## CodeQL

[`.github/workflows/codeql.yml`](../.github/workflows/codeql.yml): C# with `security-extended` on
pushes and PRs to `main`/`dev`, weekly (Monday 06:00 UTC) and on demand. The repo is public, so
results upload to **Security → Code scanning** at no cost. If GitHub's *default setup* for code
scanning is enabled, switch it off (Settings → Code security): it conflicts with this advanced setup.

## Trivy

[`.github/workflows/trivy.yml`](../.github/workflows/trivy.yml): filesystem scan for vulnerable
dependencies and committed secrets, weekly (Monday 07:00 UTC) plus pushes and PRs. Findings go to the
Security tab under category `trivy`; the job does not fail on findings.

## Dependabot

[`.github/dependabot.yml`](../.github/dependabot.yml): weekly NuGet (root, where
`Directory.Packages.props` holds every version; limit 10 open PRs) and GitHub Actions (limit 5).
`OpenTelemetry*` packages arrive as one grouped PR, as do `Npgsql` + `Npgsql.OpenTelemetry`, because
each set shares one version line. `Microsoft.Extensions.*` and `System.*` 11.x are ignored: 11.x drops
net8.0/net9.0, which this repo targets, so those stay on the newest 10.0.x.
Actions are pinned by commit SHA with a `# vX.Y.Z` comment; Dependabot updates both. Enable
**Dependabot alerts** and **security updates** under Settings → Code security.

## Analyzers

`Directory.Build.props` sets `AnalysisLevel` `latest`, `EnableNETAnalyzers` and
`EnforceCodeStyleInBuild` for every project, matching the sibling GMO repos. Warnings are not errors,
so nothing fails CI on its own; keep the Release build at 0 warnings. Severities come from
`.editorconfig` (code style) and the analyzer defaults; a project-level `NoWarn` needs a comment saying
why the rule is wrong for that code.

## Package sources

The root `nuget.config` clears inherited sources and restores from nuget.org only. This repo publishes
to the GMO GitHub Packages feed (`dotnet nuget push --source`) and must never restore from it.

## NuGet vulnerability audit

The shared `lint` job (DevOps `dotnet-lint.yml`, `fail_on_vulnerable` defaults to on) fails the build
when any direct or transitive package has a known vulnerability. Fix it by bumping the package in `Directory.Packages.props`. For a transitive one, bump
the direct dependency that pulls it in, or add a `PackageVersion` plus a direct `PackageReference`
(transitive pinning is not enabled here).

## Local commands

| Check | Command |
|-------|---------|
| Format | `dotnet format src/GMO.OpenTelemetry.sln --verify-no-changes` |
| Vulnerabilities | `dotnet list src/GMO.OpenTelemetry.sln package --vulnerable --include-transitive` |
| Coverage + HTML report + floor (net10.0) | `pwsh -NoProfile -File tst/Invoke-Coverage.ps1` |
| Workflow lint | `docker run --rm -v "${PWD}:/repo" -w /repo rhysd/actionlint:1.7.12` |
