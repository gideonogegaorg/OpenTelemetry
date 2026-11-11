$scriptPath = Join-Path $PSScriptRoot 'Invoke-Coverage.ps1'
. $scriptPath

Describe 'Get-CoverageTestArguments' {
    $common = @{ Project = 'p.csproj'; Framework = 'net10.0'; Configuration = 'Release'; RunSettings = 'c.runsettings'; ResultsDirectory = 'out' }

    It 'passes the framework, settings and coverlet collector to dotnet test' {
        $arguments = Get-CoverageTestArguments @common
        ($arguments -join ' ') | Should Be 'test p.csproj -c Release --framework net10.0 --settings c.runsettings --collect:XPlat Code Coverage --results-directory out --verbosity minimal'
    }

    It 'keeps the collector name as one argument' {
        $arguments = Get-CoverageTestArguments @common
        $arguments -contains '--collect:XPlat Code Coverage' | Should Be $true
    }

    It 'appends --no-build only when asked' {
        (Get-CoverageTestArguments @common) -contains '--no-build' | Should Be $false
        (Get-CoverageTestArguments @common -NoBuild)[-1] | Should Be '--no-build'
    }
}

Describe 'Join-ReportPaths' {
    It 'joins with semicolons and uses forward slashes' {
        Join-ReportPaths -Paths 'C:\a\coverage.opencover.xml', 'C:\b\coverage.opencover.xml' | Should Be 'C:/a/coverage.opencover.xml;C:/b/coverage.opencover.xml'
    }

    It 'fails when no report was produced' {
        { Join-ReportPaths -Paths @() } | Should Throw 'No coverage.opencover.xml'
    }
}

Describe 'Get-CoverageSummary' {
    It 'reads line, branch and method percentages' {
        $summary = Get-CoverageSummary -Lines @(
            'Summary',
            '  Assemblies: 5',
            '  Line coverage: 85.2%',
            '  Branch coverage: 71%',
            '  Method coverage: 90.4%',
            '  Full method coverage: 80%'
        )
        $summary.Line | Should Be 85.2
        $summary.Branch | Should Be 71
        $summary.Method | Should Be 90.4
    }

    It 'accepts the blank lines reportgenerator writes between sections' {
        $summary = Get-CoverageSummary -Lines 'Summary', '', '  Line coverage: 93.7%', '  Branch coverage: 88.1% (1275 of 1446)', ''
        $summary.Line | Should Be 93.7
        $summary.Branch | Should Be 88.1
    }

    It 'ignores full method coverage' {
        $summary = Get-CoverageSummary -Lines '  Line coverage: 10%', '  Full method coverage: 99%'
        $summary.Method | Should Be $null
    }

    It 'fails when the line coverage entry is missing' {
        { Get-CoverageSummary -Lines 'Summary', '  Assemblies: 5' } | Should Throw 'Line coverage'
    }
}

Describe 'Test-CoverageThreshold' {
    It 'passes metrics at or above their minimum' {
        $results = @(Test-CoverageThreshold -Summary ([pscustomobject] @{ Line = 95; Branch = 92.5; Method = 50 }) -MinLineCoverage 95 -MinBranchCoverage 92)
        $results.Count | Should Be 2
        ($results | Where-Object { -not $_.Passed }).Count | Should Be 0
    }

    It 'fails only the metric below its minimum' {
        $results = Test-CoverageThreshold -Summary ([pscustomobject] @{ Line = 96.1; Branch = 91.9 }) -MinLineCoverage 95 -MinBranchCoverage 92
        ($results | Where-Object Metric -eq 'Line').Passed | Should Be $true
        ($results | Where-Object Metric -eq 'Branch').Passed | Should Be $false
    }

    It 'fails a metric the summary does not report' {
        $results = Test-CoverageThreshold -Summary ([pscustomobject] @{ Line = 99; Branch = $null }) -MinLineCoverage 95 -MinBranchCoverage 92
        ($results | Where-Object Metric -eq 'Branch').Passed | Should Be $false
    }

    It 'fails line coverage below the minimum' {
        $results = Test-CoverageThreshold -Summary ([pscustomobject] @{ Line = 94.99; Branch = 99 }) -MinLineCoverage 95 -MinBranchCoverage 92
        ($results | Where-Object Metric -eq 'Line').Passed | Should Be $false
    }
}

Describe 'Format-CoverageThreshold' {
    It 'prints OK for a passing metric' {
        Format-CoverageThreshold -Result ([pscustomobject] @{ Metric = 'Line'; Actual = 96.5; Minimum = 95; Passed = $true }) | Should Be 'OK: line coverage 96.5% (minimum 95%)'
    }

    It 'prints FAILED for a failing metric' {
        Format-CoverageThreshold -Result ([pscustomobject] @{ Metric = 'Branch'; Actual = 90.25; Minimum = 92; Passed = $false }) | Should Be 'FAILED: branch coverage 90.25% (minimum 92%)'
    }

    It 'reports a missing metric' {
        Format-CoverageThreshold -Result ([pscustomobject] @{ Metric = 'Branch'; Actual = $null; Minimum = 92; Passed = $false }) | Should Be 'FAILED: branch coverage missing (minimum 92%)'
    }
}
