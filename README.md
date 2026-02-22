# GMO.OpenTelemetry

A .NET 10 library that provides OpenTelemetry instrumentation (runtime, process, custom CPU %) for .NET 10+ services.

This repository contains two NuGet packages:
- **GMO.OpenTelemetry**: Core library (net10.0)
- **GMO.OpenTelemetry.Serilog**: Serilog integration (net10.0)

## Table of Contents

1. [Prerequisites](#prerequisites)
2. [Getting Started](#getting-started)
3. [Configuration](#configuration)
4. [Usage](#usage)
5. [Building & Packaging](#building--packaging)
6. [CI/CD Pipeline](#cicd-pipeline)
7. [Contributing](#contributing)
8. [License](#license)

## Prerequisites

- **.NET 10 SDK**
- **OpenTelemetry .NET SDK** (transparent package references)

## Getting Started

Clone and build:

```bash
git clone https://github.com/gideonogega/OpenTelemetry.git
cd OpenTelemetry
dotnet restore src/GMO.OpenTelemetry.sln
dotnet build src/GMO.OpenTelemetry.sln -c Release
```

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

This project **does not** include unit tests. It only needs to build and pack:

```bash
# Build
dotnet build src/GMO.OpenTelemetry.sln -c Release

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
3. Run `dotnet format src/GMO.OpenTelemetry.sln` before committing to fix formatting issues
4. Commit, push to your fork, and open a pull request against `main`
5. Ensure CI passes (lint + build)

## License

Proprietary to Bullhorn, Inc. All rights reserved.
