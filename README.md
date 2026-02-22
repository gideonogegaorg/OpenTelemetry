# GMO.OpenTelemetry

A .NET 10 library that provides OpenTelemetry instrumentation (runtime, process, custom CPU %) for .NET 10+ services.

This repository contains two NuGet packages:
- **GMO.OpenTelemetry**: Core library (net10.0)
- **GMO.OpenTelemetry.Serilog**: Serilog integration (net10.0)

## Table of Contents

1. [Prerequisites](#prerequisites)
2. [Getting Started](#getting-started)
3. [Installing the packages](#installing-the-packages)
4. [Configuration](#configuration)
5. [Usage](#usage)
6. [Building & Packaging](#building--packaging)
7. [CI/CD Pipeline](#cicd-pipeline)
8. [Contributing](#contributing)
9. [License](#license)

## Prerequisites

- **.NET 10 SDK**
- **OpenTelemetry .NET SDK** (transparent package references)

**Important:** Always run `dotnet format src/GMO.OpenTelemetry.sln` before build, test, or commit. CI runs `dotnet format --verify-no-changes` and will fail if encoding or style differs.

## Getting Started

Clone, format, and build:

```bash
git clone https://github.com/gideonogegaorg/OpenTelemetry.git
cd OpenTelemetry
dotnet format src/GMO.OpenTelemetry.sln
dotnet restore src/GMO.OpenTelemetry.sln
dotnet build src/GMO.OpenTelemetry.sln -c Release
```

## Installing the packages

Packages are published to **GitHub Packages**. The feed requires authentication to read (username + Personal Access Token). Prefer **environment variables** for the PAT so credentials are never stored in `nuget.config` or any file that might be committed to a repo.

**Terms:** `{REPO_OWNER}` = the GitHub user or org that owns the feed (e.g. `gideonogegaorg`) (where the packages are published).

1. **Create a PAT (Personal Access Token)**  
   GitHub → Settings → Developer settings → Personal access tokens → [Tokens (classic)](https://github.com/settings/tokens). Create a token with scope **`read:packages`** (and optionally `repo` if the packages are in a private repo).

2. **Add the feed** using one of the options below.

   **Option A — nuget.config (recommended)**  
   Set **GITHUB_USERNAME** and **GITHUB_PAT** in your environment, then add a `nuget.config` that defines the GMO source and credentials from those env vars. Replace `{REPO_OWNER}` in the source URL. If your tooling expands env vars in the config (e.g. a script or CI), the `packageSourceCredentials` values will be substituted. `packageSourceMapping` sends `GMO.*` packages to the GitHub feed and everything else to nuget.org. Do not commit a config that contains literal secrets—only the template with `%GITHUB_USERNAME%` / `%GITHUB_PAT%` placeholders.

   ```bash
   # PowerShell
   $env:GITHUB_USERNAME = "your_github_username"
   $env:GITHUB_PAT = "your_pat_here"
   # Bash: export GITHUB_USERNAME=... GITHUB_PAT=...
   ```

   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <configuration>
     <packageSources>
       <add key="GMO" value="https://nuget.pkg.github.com/{REPO_OWNER}/index.json" />
       <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
     </packageSources>
     <packageSourceCredentials>
       <GMO>
         <add key="Username" value="%GITHUB_USERNAME%" />
         <add key="ClearTextPassword" value="%GITHUB_PAT%" />
       </GMO>
     </packageSourceCredentials>
     <packageSourceMapping>
       <packageSource key="GMO">
         <package pattern="GMO.*" />
       </packageSource>
       <packageSource key="nuget.org">
         <package pattern="*" />
       </packageSource>
     </packageSourceMapping>
   </configuration>
   ```

   **Option B — CLI (alternative)**  
   Set username and PAT in your environment (do not put them in `nuget.config`). Add the NuGet source via CLI so the credential is stored in your user-level config (e.g. Windows Credential Manager), not in repo files:

   ```bash
   # PowerShell
   $env:GITHUB_USERNAME = "your_github_username"
   $env:GITHUB_PAT = "your_pat_here"
   # Bash: export GITHUB_USERNAME=... GITHUB_PAT=...
   ```

   ```bash
   dotnet nuget add source "https://nuget.pkg.github.com/{REPO_OWNER}/index.json" \
     --name GMO \
     --username $env:GITHUB_USERNAME \
     --password $env:GITHUB_PAT
   ```

   Replace `{REPO_OWNER}` in the URL. Use `%GITHUB_USERNAME%` and `%GITHUB_PAT%` on Windows Command Prompt, or `$GITHUB_USERNAME` and `$GITHUB_PAT` in Bash. Omit `--store-password-in-clear-text` so the credential is stored encrypted.

3. **Restore** in your app:

   ```bash
   dotnet restore
   ```

   With the feed added (Option A or B), `dotnet restore` will use it when your project references `GMO.OpenTelemetry` or `GMO.OpenTelemetry.Serilog`. Keep secrets in env vars or credential manager; do not commit a config with literal credentials.

## Configuration

Add OTLP configuration to your host's settings (e.g. `appsettings.json`):

```json
"Telemetry": {
  "Enabled": true,
  "Otlp": {
    "Endpoint": "https://otlp.nr-data.net:4317/v1/traces",
    "Headers": "api-key=YOUR_LICENSE_KEY",
    "Protocol": "Grpc"
  },
  "MetricsEndpoint": "https://otlp.nr-data.net:4317/v1/metrics"
}
```

## Usage

Register OpenTelemetry and the custom CPU % gauge in your startup:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(b =>
    {
        b.AddRuntimeInstrumentation()
         .AddProcessInstrumentation()
         .AddMeter(GMO.OpenTelemetry.CustomMetrics.Name)
         .AddOtlpExporter();
    });

// Ensure CustomMetrics is constructed (implements IHostedService, starts automatically):
builder.Services.AddSingleton<CustomMetrics>();
```

## Building & Packaging

```bash
# Format first (required before every build, test, or commit)
dotnet format src/GMO.OpenTelemetry.sln

# Build
dotnet build src/GMO.OpenTelemetry.sln -c Release

# Run tests
dotnet test src/GMO.OpenTelemetry.sln -c Release --no-build

# Pack into NuGet packages
dotnet pack src/GMO.OpenTelemetry.sln -c Release -o nupkgs /p:PackageVersion=1.0.0
```

## CI/CD Pipeline

GitHub Actions (`.github/workflows/ci.yml`) on push or PR to `main` or `dev`. Order: **lint → build → test → tag (if successful) → publish**.

1. **Lint**: `dotnet format --verify-no-changes`
2. **Build**: restore, build with version from run (year.month.build.revision; `dev` uses a DEV- prefixed informational version)
3. **Test**: `dotnet test` (step passes when no test projects exist yet; add test projects to run tests)
4. **Tag** (on push only, after test passes): create and push tag `{branch}-{year}-{month}-{run_id}`
5. **Publish** (on push to main/dev only, after tag): pack and push to GitHub Packages. **main** → version `{year}.{month}.{build}.{revision}`; **dev** → same with `-alpha` suffix. Requires `GH_CLASSIC_PAT` secret.

## Contributing

1. Fork and create a branch
2. Make changes following .editorconfig style (4 spaces, camelCase with `_` prefix for private fields)
3. **Run `dotnet format src/GMO.OpenTelemetry.sln` before every build, test, or commit.** Use `--verify-no-changes` to confirm nothing is left to fix.
4. Commit, push to your fork, and open a pull request against `main`
5. Ensure CI passes (lint, build, test)

## License

This project is licensed under the GNU General Public License v3.0. See [LICENSE](LICENSE) for the full text.
