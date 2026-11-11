#Requires -Version 5.1
<#
.SYNOPSIS
Validates the TestApps end-to-end against the local OTLP collector in docker-compose.yml.

.DESCRIPTION
Starts a fresh collector, runs each app x framework once with Telemetry__* overrides that point every
export at http://localhost:4318, copies the collector's otel.json out of the container and checks that
every expected service sent every required signal. Exits non-zero if a run failed or a signal is missing.

With -RequireDbSpans it also starts the SQL Server and PostgreSQL containers (compose profile "db"), waits
for their healthchecks, hands the apps connection strings for them, and checks that every service sent
SqlClient spans and every net8.0+ service sent Npgsql spans (Npgsql has no .NET Framework build).

The machine-level Telemetry__* environment variables win over appsettings.json, so the overrides are
set on this process only for the duration of each child run and then restored.

.EXAMPLE
pwsh -NoProfile -File tst/TestApps/Invoke-TestAppValidation.ps1 -Build -Frameworks net8.0,net48

.EXAMPLE
pwsh -NoProfile -File tst/TestApps/Invoke-TestAppValidation.ps1 -Build -RequireDbSpans
#>
[CmdletBinding()]
param(
    [string[]] $Apps = @('Serilog', 'Log4Net'),

    [string[]] $Frameworks = @('net48', 'net8.0', 'net9.0', 'net10.0'),

    [string] $Configuration = 'Release',

    [switch] $Build,

    [switch] $KeepCollector,

    [switch] $RequireDbSpans,

    [string[]] $RequiredSignals = @('traces', 'metrics', 'logs'),

    [hashtable] $ExtraEnvironment = @{},

    [ValidateRange(1, 3600)]
    [int] $TimeoutSeconds = 120,

    [int] $BasePort = 5180,

    [string] $OutputDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) 'gmo-otel-testapp-validation')
)

$script:CollectorContainer = 'gmo-otel-testapps-collector'
$script:SignalKeys = [ordered]@{ resourceSpans = 'traces'; resourceMetrics = 'metrics'; resourceLogs = 'logs' }
# Must match the mssql / postgres services in docker-compose.yml.
$script:SqlServerConnectionString = 'Server=localhost,14333;User ID=sa;Password=GmoOtel_Test1!;Encrypt=True;TrustServerCertificate=True;Connect Timeout=30'
$script:PostgresConnectionString = 'Host=localhost;Port=54333;Username=gmo;Password=GmoOtel_Test1!;Database=gmo_otel;Timeout=30'
# Npgsql and OpenTelemetry.Instrumentation.SqlClient emit the stable db.system.name; db.system=mssql is the
# older SqlClient convention, still accepted so an instrumentation downgrade does not read as missing spans.
$script:DbSystemClients = @{ 'mssql' = 'SqlClient'; 'microsoft.sql_server' = 'SqlClient'; 'postgresql' = 'Npgsql' }

function ConvertTo-ValidatedList {
    # pwsh -File passes "a,b" as one string, so lists are split here instead of relying on ValidateSet.
    param([Parameter(Mandatory)] [string] $Name, [string[]] $Value, [Parameter(Mandatory)] [string[]] $Allowed)
    $items = @($Value | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if ($items.Count -eq 0) { throw "ERROR: -$Name needs at least one of: $($Allowed -join ', ')" }
    foreach ($item in $items) {
        $match = $Allowed | Where-Object { $_ -eq $item } | Select-Object -First 1
        if (-not $match) { throw "ERROR: -$Name value '$item' is not one of: $($Allowed -join ', ')" }
        $match
    }
}

function Get-TestAppServiceName {
    param([Parameter(Mandatory)] [string] $App, [Parameter(Mandatory)] [string] $Framework)
    'otel-testapp-{0}-{1}' -f $App.ToLowerInvariant(), ($Framework -replace '\.0$', '')
}

function Get-TestAppDbEnvironment {
    [ordered]@{
        'Telemetry__EnableSqlInstrumentation'     = 'true'
        'Telemetry__EnableSqlInstrumentationText' = 'true'
        'Telemetry__SqlConnectionString'          = $script:SqlServerConnectionString
        'Telemetry__PostgresConnectionString'     = $script:PostgresConnectionString
    }
}

function Get-TestAppTelemetryEnvironment {
    param([hashtable] $Extra = @{}, [switch] $Database)
    $environment = [ordered]@{
        'Telemetry__Enabled'          = 'true'
        'Telemetry__EnableLogging'    = 'true'
        'Telemetry__EnableConsole'    = 'false'
        'Telemetry__Otlp__Endpoint'   = 'http://localhost:4318'
        'Telemetry__Otlp__Protocol'   = 'HttpProtobuf'
        'Telemetry__Otlp__Headers'    = ''
        'Telemetry__LoggingEndpoint'  = 'http://localhost:4318'
        'Telemetry__MetricsEndpoint'  = 'http://localhost:4318'
        'Telemetry__SqlConnectionString'      = ''
        'Telemetry__PostgresConnectionString' = ''
    }
    if ($Database) {
        $db = Get-TestAppDbEnvironment
        foreach ($key in $db.Keys) { $environment[$key] = $db[$key] }
    }
    foreach ($key in $Extra.Keys) { $environment[[string]$key] = [string]$Extra[$key] }
    $environment
}

function Set-ProcessEnvironment {
    param([Parameter(Mandatory)] [System.Collections.IDictionary] $Values)
    $previous = @{}
    foreach ($key in $Values.Keys) {
        $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        # An empty value deletes the variable on Windows, letting appsettings.json supply it again; a single
        # space overrides it and the library treats whitespace-only settings as unset.
        $value = if ([string]::IsNullOrEmpty($Values[$key])) { ' ' } else { $Values[$key] }
        [Environment]::SetEnvironmentVariable($key, $value, 'Process')
    }
    $previous
}

function Restore-ProcessEnvironment {
    param([Parameter(Mandatory)] [hashtable] $Previous)
    foreach ($key in $Previous.Keys) {
        [Environment]::SetEnvironmentVariable($key, $Previous[$key], 'Process')
    }
}

function Get-SignalMatrix {
    param([Parameter(Mandatory)] [string] $Path)
    $matrix = @{}
    foreach ($line in [System.IO.File]::ReadLines((Resolve-Path -LiteralPath $Path).ProviderPath)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $record = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }
        if ($null -eq $record) { continue }
        foreach ($key in $script:SignalKeys.Keys) {
            $resources = $record.PSObject.Properties[$key]
            if ($null -eq $resources) { continue }
            foreach ($resource in @($resources.Value)) {
                $service = $null
                if ($resource.resource -and $resource.resource.attributes) {
                    $attribute = @($resource.resource.attributes) | Where-Object { $_.key -eq 'service.name' } | Select-Object -First 1
                    if ($attribute -and $attribute.value) { $service = $attribute.value.stringValue }
                }
                if ([string]::IsNullOrWhiteSpace($service)) { continue }
                if (-not $matrix.ContainsKey($service)) {
                    $matrix[$service] = [ordered]@{ traces = $false; metrics = $false; logs = $false }
                }
                $matrix[$service][$script:SignalKeys[$key]] = $true
            }
        }
    }
    $matrix
}

function Compare-SignalMatrix {
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [hashtable] $Matrix,
        [Parameter(Mandatory)] [string[]] $ExpectedServices,
        [Parameter(Mandatory)] [string[]] $RequiredSignals
    )
    foreach ($service in $ExpectedServices) {
        foreach ($signal in $RequiredSignals) {
            $seen = $Matrix.ContainsKey($service) -and $Matrix[$service][$signal]
            [pscustomobject]@{
                Service = $service
                Signal  = $signal
                Status  = if ($seen) { 'OK' } else { 'MISSING' }
            }
        }
    }
}

function Get-AttributeValue {
    param($Attributes, [Parameter(Mandatory)] [string] $Key)
    $attribute = @($Attributes) | Where-Object { $_ -and $_.key -eq $Key } | Select-Object -First 1
    if ($attribute -and $attribute.value) { $attribute.value.stringValue }
}

function Get-DbSpanMatrix {
    param([Parameter(Mandatory)] [string] $Path)
    $matrix = @{}
    foreach ($line in [System.IO.File]::ReadLines((Resolve-Path -LiteralPath $Path).ProviderPath)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $record = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }
        if ($null -eq $record -or $null -eq $record.PSObject.Properties['resourceSpans']) { continue }
        foreach ($resource in @($record.resourceSpans)) {
            if (-not $resource.resource) { continue }
            $service = Get-AttributeValue -Attributes $resource.resource.attributes -Key 'service.name'
            if ([string]::IsNullOrWhiteSpace($service)) { continue }
            foreach ($scope in @($resource.scopeSpans)) {
                if (-not $scope) { continue }
                foreach ($span in @($scope.spans)) {
                    if (-not $span) { continue }
                    $system = Get-AttributeValue -Attributes $span.attributes -Key 'db.system.name'
                    if (-not $system) { $system = Get-AttributeValue -Attributes $span.attributes -Key 'db.system' }
                    if (-not $system -or -not $script:DbSystemClients.ContainsKey($system)) { continue }
                    $client = $script:DbSystemClients[$system]
                    if (-not $matrix.ContainsKey($service)) { $matrix[$service] = @{} }
                    if (-not $matrix[$service].ContainsKey($client)) { $matrix[$service][$client] = [pscustomobject]@{ Spans = 0; Params = $false } }
                    $entry = $matrix[$service][$client]
                    $entry.Spans++
                    if (Get-AttributeValue -Attributes $span.attributes -Key 'db.params') { $entry.Params = $true }
                }
            }
        }
    }
    $matrix
}

function Get-ExpectedDbClients {
    param([Parameter(Mandatory)] [string] $Framework)
    if ($Framework -eq 'net48') { @('SqlClient') } else { @('SqlClient', 'Npgsql') }
}

function Compare-DbSpanMatrix {
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [hashtable] $Matrix,
        [Parameter(Mandatory)] [object[]] $Expectations
    )
    foreach ($expectation in $Expectations) {
        foreach ($client in @($expectation.Clients)) {
            $entry = $null
            if ($Matrix.ContainsKey($expectation.Service) -and $Matrix[$expectation.Service].ContainsKey($client)) {
                $entry = $Matrix[$expectation.Service][$client]
            }
            [pscustomobject]@{
                Service = $expectation.Service
                Client  = $client
                Spans   = if ($entry) { $entry.Spans } else { 0 }
                Params  = if ($entry) { $entry.Params } else { $false }
                Status  = if ($entry -and $entry.Spans -gt 0) { 'OK' } else { 'MISSING' }
            }
        }
    }
}

function Invoke-Docker {
    param([Parameter(Mandatory)] [string[]] $Arguments, [switch] $AllowFailure)
    & docker @Arguments 2>&1 | ForEach-Object { Write-Verbose "docker: $_" }
    if ($LASTEXITCODE -ne 0 -and -not $AllowFailure) {
        throw "ERROR: docker $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Wait-TcpPort {
    param([string] $HostName = 'localhost', [Parameter(Mandatory)] [int] $Port, [int] $TimeoutSeconds = 60)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $client = New-Object System.Net.Sockets.TcpClient
        try {
            $connect = $client.BeginConnect($HostName, $Port, $null, $null)
            if ($connect.AsyncWaitHandle.WaitOne(1000) -and $client.Connected) { return }
        } catch {
        } finally {
            $client.Close()
        }
        Start-Sleep -Milliseconds 500
    }
    throw "ERROR: port $Port on $HostName did not accept connections within $TimeoutSeconds s"
}

function Get-TestAppLaunch {
    param([Parameter(Mandatory)] [string] $ProjectDirectory, [Parameter(Mandatory)] [string] $App, [Parameter(Mandatory)] [string] $Framework, [Parameter(Mandatory)] [string] $Configuration, [Parameter(Mandatory)] [int] $Port)
    $name = "TestApp.$App"
    $outputDirectory = Join-Path $ProjectDirectory (Join-Path 'bin' (Join-Path $Configuration $Framework))
    if ($Framework -eq 'net48') {
        $target = Join-Path $outputDirectory "$name.exe"
        $launch = @{ FilePath = $target; ArgumentList = @('--port', "$Port") }
    } else {
        $target = Join-Path $outputDirectory "$name.dll"
        $launch = @{ FilePath = 'dotnet'; ArgumentList = @("`"$target`"", '--port', "$Port") }
    }
    $launch.Target = $target
    $launch.WorkingDirectory = $outputDirectory
    $launch
}

function Invoke-TestAppRun {
    param([Parameter(Mandatory)] [hashtable] $Launch, [Parameter(Mandatory)] [string] $LogPath, [Parameter(Mandatory)] [int] $TimeoutSeconds)
    $errorLog = [System.IO.Path]::ChangeExtension($LogPath, '.err.log')
    $process = Start-Process -FilePath $Launch.FilePath -ArgumentList $Launch.ArgumentList -WorkingDirectory $Launch.WorkingDirectory -RedirectStandardOutput $LogPath -RedirectStandardError $errorLog -NoNewWindow -PassThru
    # Touching Handle keeps the process handle open so ExitCode is still readable after exit.
    $null = $process.Handle
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        try { $process.Kill() } catch { }
        return 'TIMEOUT'
    }
    $process.WaitForExit()
    $process.ExitCode
}

function Format-ResultTable {
    param([Parameter(Mandatory)] [object[]] $Rows)
    ($Rows | Format-Table -AutoSize | Out-String -Width 200).TrimEnd()
}

function Invoke-TestAppValidation {
    $Apps = @(ConvertTo-ValidatedList -Name 'Apps' -Value $Apps -Allowed 'Serilog', 'Log4Net')
    $Frameworks = @(ConvertTo-ValidatedList -Name 'Frameworks' -Value $Frameworks -Allowed 'net48', 'net8.0', 'net9.0', 'net10.0')
    $RequiredSignals = @(ConvertTo-ValidatedList -Name 'RequiredSignals' -Value $RequiredSignals -Allowed 'traces', 'metrics', 'logs')
    $composeFile = Join-Path $PSScriptRoot 'docker-compose.yml'
    $null = New-Item -ItemType Directory -Force -Path $OutputDirectory
    $runStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $runDirectory = Join-Path $OutputDirectory $runStamp
    $null = New-Item -ItemType Directory -Force -Path $runDirectory
    Write-Host "INFO: logs and results in $runDirectory"

    $plan = @()
    $port = $BasePort
    foreach ($app in $Apps) {
        $projectDirectory = Join-Path $PSScriptRoot "TestApp.$app"
        foreach ($framework in $Frameworks) {
            $plan += [pscustomobject]@{
                App       = $app
                Framework = $framework
                Service   = Get-TestAppServiceName -App $app -Framework $framework
                Project   = Join-Path $projectDirectory "TestApp.$app.csproj"
                Launch    = Get-TestAppLaunch -ProjectDirectory $projectDirectory -App $app -Framework $framework -Configuration $Configuration -Port $port
            }
            $port++
        }
    }

    if ($Build) {
        foreach ($item in $plan) {
            Write-Host "INFO: building $($item.App) $($item.Framework)"
            & dotnet build $item.Project -c $Configuration -f $item.Framework --nologo -v quiet | Out-File -FilePath (Join-Path $runDirectory "build-$($item.App)-$($item.Framework).log") -Encoding utf8
            if ($LASTEXITCODE -ne 0) { throw "ERROR: build failed for $($item.App) $($item.Framework); see $runDirectory" }
        }
    }

    $missing = @($plan | Where-Object { -not (Test-Path -LiteralPath $_.Launch.Target) })
    if ($missing.Count -gt 0) {
        $list = ($missing | ForEach-Object { "  $($_.Launch.Target)" }) -join [Environment]::NewLine
        throw "ERROR: build output missing (pass -Build or build -c $Configuration first):$([Environment]::NewLine)$list"
    }

    $hostOutput = Join-Path $PSScriptRoot 'otel-out/otel.json'
    $runs = @()
    $matrix = @{}
    $dbMatrix = @{}
    # --profile db on down also removes database containers left by an earlier -RequireDbSpans run.
    $compose = @('compose', '-f', $composeFile, '--profile', 'db')
    try {
        Write-Host 'INFO: starting a fresh collector'
        Invoke-Docker -Arguments ($compose + 'down') -AllowFailure
        if (Test-Path -LiteralPath $hostOutput) { Remove-Item -LiteralPath $hostOutput -Force -ErrorAction SilentlyContinue }
        if ($RequireDbSpans) {
            Write-Host 'INFO: starting SQL Server and PostgreSQL and waiting for their healthchecks'
            Invoke-Docker -Arguments ($compose + @('up', '-d', '--wait', '--wait-timeout', '300'))
            Write-Host 'INFO: databases healthy on 14333 (SQL Server) and 54333 (PostgreSQL)'
        } else {
            Invoke-Docker -Arguments @('compose', '-f', $composeFile, 'up', '-d')
        }
        Wait-TcpPort -Port 4318 -TimeoutSeconds 60
        Write-Host 'INFO: collector accepting connections on 4318'

        $environment = Get-TestAppTelemetryEnvironment -Extra $ExtraEnvironment -Database:$RequireDbSpans
        Write-Host "INFO: child environment overrides: $(@($environment.Keys) -join ', ')"
        foreach ($item in $plan) {
            $logPath = Join-Path $runDirectory "run-$($item.App)-$($item.Framework).log"
            Write-Host "INFO: running $($item.Service)"
            $previous = Set-ProcessEnvironment -Values $environment
            try {
                $exitCode = Invoke-TestAppRun -Launch $item.Launch -LogPath $logPath -TimeoutSeconds $TimeoutSeconds
            } catch {
                $exitCode = "ERROR: $($_.Exception.Message)"
            } finally {
                Restore-ProcessEnvironment -Previous $previous
            }
            $runs += [pscustomobject]@{
                App       = $item.App
                Framework = $item.Framework
                ExitCode  = $exitCode
                Status    = if ($exitCode -is [int] -and $exitCode -eq 0) { 'OK' } else { 'FAILED' }
                Log       = $logPath
            }
        }

        Start-Sleep -Seconds 5
        $resultPath = Join-Path $runDirectory 'otel.json'
        Invoke-Docker -Arguments @('cp', "$($script:CollectorContainer):/output/otel.json", $resultPath)
        $matrix = Get-SignalMatrix -Path $resultPath
        if ($RequireDbSpans) { $dbMatrix = Get-DbSpanMatrix -Path $resultPath }
        Write-Host "INFO: collector output copied to $resultPath"
    } finally {
        if ($KeepCollector) {
            Write-Host "INFO: collector left running ($script:CollectorContainer)"
        } else {
            Invoke-Docker -Arguments ($compose + 'down') -AllowFailure
        }
    }

    $comparison = @(Compare-SignalMatrix -Matrix $matrix -ExpectedServices @($plan.Service) -RequiredSignals $RequiredSignals)
    $dbComparison = @()
    if ($RequireDbSpans) {
        $expectations = @($plan | ForEach-Object { [pscustomobject]@{ Service = $_.Service; Clients = Get-ExpectedDbClients -Framework $_.Framework } })
        $dbComparison = @(Compare-DbSpanMatrix -Matrix $dbMatrix -Expectations $expectations)
    }

    Write-Host ''
    Write-Host 'Runs'
    Write-Host (Format-ResultTable -Rows $runs)
    Write-Host ''
    Write-Host 'Signals'
    Write-Host (Format-ResultTable -Rows $comparison)
    Write-Host ''
    if ($RequireDbSpans) {
        Write-Host 'DB spans'
        Write-Host (Format-ResultTable -Rows $dbComparison)
        Write-Host ''
    }

    $failedRuns = @($runs | Where-Object Status -ne 'OK').Count
    $missingSignals = @($comparison | Where-Object Status -ne 'OK').Count
    $missingDb = @($dbComparison | Where-Object Status -ne 'OK').Count
    if ($failedRuns -gt 0 -or $missingSignals -gt 0 -or $missingDb -gt 0) {
        Write-Host "FAILED: $failedRuns failed run(s), $missingSignals missing signal(s), $missingDb missing DB span set(s)"
        return 1
    }
    $dbSummary = if ($RequireDbSpans) { ' and the expected DB spans' } else { '' }
    Write-Host "SUCCESS: $($runs.Count) run(s), every expected service sent $($RequiredSignals -join ', ')$dbSummary"
    0
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        exit (Invoke-TestAppValidation)
    } catch {
        Write-Host $_.Exception.Message
        exit 2
    }
}
