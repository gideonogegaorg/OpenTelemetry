#Requires -Version 5.1
<#
.SYNOPSIS
Runs the unit tests for one framework with coverlet and renders an HTML coverage report.

.DESCRIPTION
Uses the repo-root coverlet.runsettings (OpenCover; test and TestApp assemblies excluded), the same
settings the CI sonar job uses. Only net8.0+ is offered: coverlet collects nothing under Mono, and net48
skips the code paths behind #if NET, so its numbers are not comparable.

reportgenerator comes from the local tool manifest (.config/dotnet-tools.json); no global install needed.
Raw results go to <OutputDirectory>/raw and the report to <OutputDirectory>/report. Both are wiped at the
start of each run.

coverlet.collector has no threshold support, so the floor is enforced here: line and branch coverage
below -MinLineCoverage / -MinBranchCoverage fail the run. Exit codes: 0 pass, 1 tests failed, 2 setup or
report error, 3 coverage below the floor.

.EXAMPLE
pwsh -NoProfile -File tst/Invoke-Coverage.ps1

.EXAMPLE
pwsh -NoProfile -File tst/Invoke-Coverage.ps1 -Framework net8.0 -NoBuild

.EXAMPLE
pwsh -NoProfile -File tst/Invoke-Coverage.ps1 -MinLineCoverage 90 -MinBranchCoverage 85
#>
[CmdletBinding()]
param(
    [ValidateSet('net8.0', 'net9.0', 'net10.0')]
    [string] $Framework = 'net10.0',

    [string] $Configuration = 'Release',

    [switch] $NoBuild,

    [ValidateRange(0, 100)]
    [double] $MinLineCoverage = 95,

    [ValidateRange(0, 100)]
    [double] $MinBranchCoverage = 92,

    [string] $OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'coverage')
)

$script:RepoRoot = Split-Path $PSScriptRoot -Parent
$script:TestProject = Join-Path $script:RepoRoot 'tst/GMO.OpenTelemetry.Tests/GMO.OpenTelemetry.Tests.csproj'
$script:RunSettings = Join-Path $script:RepoRoot 'coverlet.runsettings'

function Get-CoverageTestArguments {
    param(
        [Parameter(Mandatory)] [string] $Project,
        [Parameter(Mandatory)] [string] $Framework,
        [Parameter(Mandatory)] [string] $Configuration,
        [Parameter(Mandatory)] [string] $RunSettings,
        [Parameter(Mandatory)] [string] $ResultsDirectory,
        [switch] $NoBuild
    )
    $arguments = @(
        'test', $Project,
        '-c', $Configuration,
        '--framework', $Framework,
        '--settings', $RunSettings,
        '--collect:XPlat Code Coverage',
        '--results-directory', $ResultsDirectory,
        '--verbosity', 'minimal'
    )
    if ($NoBuild) {
        $arguments += '--no-build'
    }
    $arguments
}

function Join-ReportPaths {
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [string[]] $Paths)
    if ($Paths.Count -eq 0) {
        throw 'No coverage.opencover.xml was produced. Check that coverlet.collector is referenced and the run used coverlet.runsettings.'
    }
    ($Paths | ForEach-Object { $_.Replace('\', '/') }) -join ';'
}

function Get-CoverageSummary {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string[]] $Lines)
    $summary = [ordered]@{ Line = $null; Branch = $null; Method = $null }
    foreach ($line in $Lines) {
        if ($line -match '^\s*(Line|Branch|Method) coverage:\s*([0-9.]+)%') {
            $summary[$Matches[1]] = [double]::Parse($Matches[2], [System.Globalization.CultureInfo]::InvariantCulture)
        }
    }
    if ($null -eq $summary.Line) {
        throw 'The reportgenerator summary has no "Line coverage:" entry.'
    }
    [pscustomobject] $summary
}

function Test-CoverageThreshold {
    param(
        [Parameter(Mandatory)] $Summary,
        [Parameter(Mandatory)] [double] $MinLineCoverage,
        [Parameter(Mandatory)] [double] $MinBranchCoverage
    )
    foreach ($metric in @(@{ Name = 'Line'; Minimum = $MinLineCoverage }, @{ Name = 'Branch'; Minimum = $MinBranchCoverage })) {
        $actual = $Summary.($metric.Name)
        [pscustomobject] @{
            Metric  = $metric.Name
            Actual  = $actual
            Minimum = $metric.Minimum
            Passed  = $null -ne $actual -and $actual -ge $metric.Minimum
        }
    }
}

function Format-CoverageThreshold {
    param([Parameter(Mandatory)] $Result)
    $status = if ($Result.Passed) { 'OK' } else { 'FAILED' }
    $actual = if ($null -eq $Result.Actual) { 'missing' } else { '{0}%' -f $Result.Actual.ToString([System.Globalization.CultureInfo]::InvariantCulture) }
    '{0}: {1} coverage {2} (minimum {3}%)' -f $status, $Result.Metric.ToLowerInvariant(), $actual, $Result.Minimum.ToString([System.Globalization.CultureInfo]::InvariantCulture)
}

function Invoke-Coverage {
    $rawDirectory = Join-Path $OutputDirectory 'raw'
    $reportDirectory = Join-Path $OutputDirectory 'report'
    foreach ($directory in $rawDirectory, $reportDirectory) {
        if (Test-Path $directory) {
            Remove-Item $directory -Recurse -Force
        }
    }

    $testArguments = Get-CoverageTestArguments -Project $script:TestProject -Framework $Framework -Configuration $Configuration -RunSettings $script:RunSettings -ResultsDirectory $rawDirectory -NoBuild:$NoBuild
    Write-Host "INFO: dotnet $($testArguments -join ' ')"
    & dotnet @testArguments | Out-Host
    $testExitCode = $LASTEXITCODE

    $reports = Join-ReportPaths -Paths @(Get-ChildItem -Path $rawDirectory -Recurse -Filter 'coverage.opencover.xml' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)

    Push-Location $script:RepoRoot
    try {
        & dotnet tool restore | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed.' }
        & dotnet tool run reportgenerator "-reports:$reports" "-targetdir:$reportDirectory" '-reporttypes:Html;TextSummary' | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'reportgenerator failed.' }
    } finally {
        Pop-Location
    }

    $summary = Get-CoverageSummary -Lines (Get-Content (Join-Path $reportDirectory 'Summary.txt'))
    Write-Host ("Coverage ({0}): line {1}%, branch {2}%, method {3}%" -f $Framework, $summary.Line, $summary.Branch, $summary.Method)
    Write-Host "Report: $(Join-Path $reportDirectory 'index.html')"

    $thresholds = @(Test-CoverageThreshold -Summary $summary -MinLineCoverage $MinLineCoverage -MinBranchCoverage $MinBranchCoverage)
    $thresholds | ForEach-Object { Write-Host (Format-CoverageThreshold -Result $_) }

    if ($testExitCode -ne 0) {
        Write-Host "FAILED: dotnet test exited $testExitCode; the report covers a failing run."
        return 1
    }
    if ($thresholds | Where-Object { -not $_.Passed }) {
        return 3
    }
    0
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        exit (Invoke-Coverage)
    } catch {
        Write-Host "ERROR: $($_.Exception.Message)"
        exit 2
    }
}
