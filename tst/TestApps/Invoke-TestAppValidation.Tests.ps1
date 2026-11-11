$scriptPath = Join-Path $PSScriptRoot 'Invoke-TestAppValidation.ps1'
. $scriptPath

function New-ResourceJson {
    param([string] $Key, [string[]] $Services)
    $resources = foreach ($service in $Services) {
        if ($service) {
            '{"resource":{"attributes":[{"key":"host.name","value":{"stringValue":"box"}},{"key":"service.name","value":{"stringValue":"' + $service + '"}}]}}'
        } else {
            '{"resource":{"attributes":[{"key":"host.name","value":{"stringValue":"box"}}]}}'
        }
    }
    '{"' + $Key + '":[' + ($resources -join ',') + ']}'
}

function Write-SampleFile {
    param([string[]] $Lines)
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("otel-sample-{0}.json" -f [guid]::NewGuid())
    [System.IO.File]::WriteAllLines($path, $Lines)
    $path
}

Describe 'ConvertTo-ValidatedList' {
    It 'splits comma-joined values and keeps the canonical casing' {
        $result = @(ConvertTo-ValidatedList -Name 'Frameworks' -Value 'NET8.0, net48', 'net10.0' -Allowed 'net48', 'net8.0', 'net10.0')
        ($result -join '|') | Should Be 'net8.0|net48|net10.0'
    }

    It 'rejects unknown values' {
        { ConvertTo-ValidatedList -Name 'Apps' -Value 'Serilog,NLog' -Allowed 'Serilog', 'Log4Net' } | Should Throw "'NLog'"
    }

    It 'rejects an empty list' {
        { ConvertTo-ValidatedList -Name 'Apps' -Value ' , ' -Allowed 'Serilog' } | Should Throw 'at least one'
    }
}

Describe 'Get-TestAppServiceName' {
    It 'drops the .0 suffix and lower-cases the app' {
        Get-TestAppServiceName -App 'Serilog' -Framework 'net8.0' | Should Be 'otel-testapp-serilog-net8'
        Get-TestAppServiceName -App 'Log4Net' -Framework 'net10.0' | Should Be 'otel-testapp-log4net-net10'
        Get-TestAppServiceName -App 'Log4Net' -Framework 'net48' | Should Be 'otel-testapp-log4net-net48'
    }
}

Describe 'Get-SignalMatrix' {
    It 'maps each resource array to its signal per service' {
        $path = Write-SampleFile @(
            (New-ResourceJson -Key 'resourceSpans' -Services 'svc-a', 'svc-b'),
            (New-ResourceJson -Key 'resourceMetrics' -Services 'svc-a'),
            (New-ResourceJson -Key 'resourceLogs' -Services 'svc-b')
        )
        try {
            $matrix = Get-SignalMatrix -Path $path
            $matrix.Count | Should Be 2
            $matrix['svc-a'].traces | Should Be $true
            $matrix['svc-a'].metrics | Should Be $true
            $matrix['svc-a'].logs | Should Be $false
            $matrix['svc-b'].traces | Should Be $true
            $matrix['svc-b'].metrics | Should Be $false
            $matrix['svc-b'].logs | Should Be $true
        } finally { Remove-Item -LiteralPath $path -Force }
    }

    It 'skips resources without service.name, blank lines and malformed lines' {
        $path = Write-SampleFile @(
            '',
            '   ',
            '{not json',
            '{"unrelated":true}',
            (New-ResourceJson -Key 'resourceLogs' -Services $null),
            (New-ResourceJson -Key 'resourceLogs' -Services 'svc-c'),
            '{"resourceSpans":[{"resource":{}}]}'
        )
        try {
            $matrix = Get-SignalMatrix -Path $path
            $matrix.Count | Should Be 1
            $matrix['svc-c'].logs | Should Be $true
            $matrix['svc-c'].traces | Should Be $false
        } finally { Remove-Item -LiteralPath $path -Force }
    }

    It 'returns an empty matrix for an empty file' {
        $path = Write-SampleFile @()
        try {
            (Get-SignalMatrix -Path $path).Count | Should Be 0
        } finally { Remove-Item -LiteralPath $path -Force }
    }
}

Describe 'Compare-SignalMatrix' {
    $full = @{
        'svc-a' = [ordered]@{ traces = $true; metrics = $true; logs = $true }
        'svc-b' = [ordered]@{ traces = $true; metrics = $true; logs = $true }
    }
    $signals = @('traces', 'metrics', 'logs')

    It 'passes when every expected service sent every required signal' {
        $rows = @(Compare-SignalMatrix -Matrix $full -ExpectedServices 'svc-a', 'svc-b' -RequiredSignals $signals)
        $rows.Count | Should Be 6
        @($rows | Where-Object Status -ne 'OK').Count | Should Be 0
    }

    It 'fails only the missing signal' {
        $partial = @{
            'svc-a' = [ordered]@{ traces = $true; metrics = $false; logs = $true }
        }
        $rows = @(Compare-SignalMatrix -Matrix $partial -ExpectedServices 'svc-a' -RequiredSignals $signals)
        $missing = @($rows | Where-Object Status -eq 'MISSING')
        $missing.Count | Should Be 1
        $missing[0].Service | Should Be 'svc-a'
        $missing[0].Signal | Should Be 'metrics'
    }

    It 'reports every signal of an absent service as missing' {
        $rows = @(Compare-SignalMatrix -Matrix @{} -ExpectedServices 'svc-z' -RequiredSignals $signals)
        @($rows | Where-Object Status -eq 'MISSING').Count | Should Be 3
    }

    It 'ignores unexpected extra services' {
        $extra = $full.Clone()
        $extra['svc-unexpected'] = [ordered]@{ traces = $false; metrics = $false; logs = $false }
        $rows = @(Compare-SignalMatrix -Matrix $extra -ExpectedServices 'svc-a' -RequiredSignals $signals)
        $rows.Count | Should Be 3
        @($rows | Where-Object Service -eq 'svc-unexpected').Count | Should Be 0
        @($rows | Where-Object Status -ne 'OK').Count | Should Be 0
    }

    It 'checks only the required signals' {
        $tracesOnly = @{ 'svc-a' = [ordered]@{ traces = $true; metrics = $false; logs = $false } }
        $rows = @(Compare-SignalMatrix -Matrix $tracesOnly -ExpectedServices 'svc-a' -RequiredSignals 'traces')
        $rows.Count | Should Be 1
        $rows[0].Status | Should Be 'OK'
    }
}

Describe 'Get-TestAppTelemetryEnvironment' {
    It 'points every endpoint at the local HTTP collector and blanks headers' {
        $environment = Get-TestAppTelemetryEnvironment
        $environment['Telemetry__Otlp__Endpoint'] | Should Be 'http://localhost:4318'
        $environment['Telemetry__LoggingEndpoint'] | Should Be 'http://localhost:4318'
        $environment['Telemetry__MetricsEndpoint'] | Should Be 'http://localhost:4318'
        $environment['Telemetry__Otlp__Protocol'] | Should Be 'HttpProtobuf'
        $environment['Telemetry__Otlp__Headers'] | Should Be ''
        $environment['Telemetry__Enabled'] | Should Be 'true'
    }

    It 'adds and overrides with extra settings' {
        $environment = Get-TestAppTelemetryEnvironment -Extra @{ 'Telemetry__Db__ConnectionString' = 'Server=x'; 'Telemetry__EnableConsole' = 'true' }
        $environment['Telemetry__Db__ConnectionString'] | Should Be 'Server=x'
        $environment['Telemetry__EnableConsole'] | Should Be 'true'
    }
}

Describe 'Set-ProcessEnvironment and Restore-ProcessEnvironment' {
    It 'overrides for the run and restores existing and absent variables' {
        $existing = 'GMO_VALIDATION_TEST_EXISTING'
        $absent = 'GMO_VALIDATION_TEST_ABSENT'
        $blank = 'GMO_VALIDATION_TEST_BLANK'
        [Environment]::SetEnvironmentVariable($existing, 'original', 'Process')
        [Environment]::SetEnvironmentVariable($absent, $null, 'Process')
        [Environment]::SetEnvironmentVariable($blank, 'secret', 'Process')
        try {
            $previous = Set-ProcessEnvironment -Values @{ $existing = 'override'; $absent = 'new'; $blank = '' }
            [Environment]::GetEnvironmentVariable($existing, 'Process') | Should Be 'override'
            [Environment]::GetEnvironmentVariable($absent, 'Process') | Should Be 'new'
            [Environment]::GetEnvironmentVariable($blank, 'Process').Trim() | Should Be ''

            Restore-ProcessEnvironment -Previous $previous
            [Environment]::GetEnvironmentVariable($existing, 'Process') | Should Be 'original'
            [Environment]::GetEnvironmentVariable($absent, 'Process') | Should BeNullOrEmpty
            [Environment]::GetEnvironmentVariable($blank, 'Process') | Should Be 'secret'
        } finally {
            foreach ($name in $existing, $absent, $blank) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
        }
    }
}

function New-SpanJson {
    param([string] $Service, [hashtable[]] $Spans)
    $spanJson = foreach ($span in $Spans) {
        $attributes = foreach ($key in $span.Keys) { '{"key":"' + $key + '","value":{"stringValue":"' + $span[$key] + '"}}' }
        '{"name":"span","attributes":[' + ($attributes -join ',') + ']}'
    }
    '{"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"' + $Service + '"}}]},"scopeSpans":[{"scope":{"name":"test"},"spans":[' + ($spanJson -join ',') + ']}]}]}'
}

Describe 'Get-TestAppTelemetryEnvironment database settings' {
    It 'blanks both connection strings by default so machine settings cannot leak in' {
        $environment = Get-TestAppTelemetryEnvironment
        $environment['Telemetry__SqlConnectionString'] | Should Be ''
        $environment['Telemetry__PostgresConnectionString'] | Should Be ''
        $environment.Contains('Telemetry__EnableSqlInstrumentation') | Should Be $false
    }

    It 'points both connection strings at the compose databases and enables SQL text enrichment' {
        $environment = Get-TestAppTelemetryEnvironment -Database
        $environment['Telemetry__SqlConnectionString'] | Should Match 'Server=localhost,14333;'
        $environment['Telemetry__PostgresConnectionString'] | Should Match 'Host=localhost;Port=54333;'
        $environment['Telemetry__EnableSqlInstrumentation'] | Should Be 'true'
        $environment['Telemetry__EnableSqlInstrumentationText'] | Should Be 'true'
    }

    It 'lets extra settings override the database settings' {
        $environment = Get-TestAppTelemetryEnvironment -Database -Extra @{ 'Telemetry__PostgresConnectionString' = 'Host=other' }
        $environment['Telemetry__PostgresConnectionString'] | Should Be 'Host=other'
        $environment['Telemetry__SqlConnectionString'] | Should Match '14333'
    }
}

Describe 'Get-DbSpanMatrix' {
    It 'maps old and stable db system attributes to the client and records db.params' {
        $path = Write-SampleFile @(
            (New-SpanJson -Service 'svc-a' -Spans @{ 'db.system' = 'mssql'; 'db.params' = '[42]' }, @{ 'db.system.name' = 'microsoft.sql_server' }),
            (New-SpanJson -Service 'svc-a' -Spans @{ 'db.system.name' = 'postgresql' }),
            (New-SpanJson -Service 'svc-b' -Spans @{ 'db.system' = 'mssql' })
        )
        try {
            $matrix = Get-DbSpanMatrix -Path $path
            $matrix.Count | Should Be 2
            $matrix['svc-a']['SqlClient'].Spans | Should Be 2
            $matrix['svc-a']['SqlClient'].Params | Should Be $true
            $matrix['svc-a']['Npgsql'].Spans | Should Be 1
            $matrix['svc-a']['Npgsql'].Params | Should Be $false
            $matrix['svc-b'].ContainsKey('Npgsql') | Should Be $false
        } finally { Remove-Item -LiteralPath $path -Force }
    }

    It 'ignores non-db spans, unknown db systems, metrics and malformed lines' {
        $path = Write-SampleFile @(
            '{not json',
            '',
            (New-ResourceJson -Key 'resourceMetrics' -Services 'svc-a'),
            (New-SpanJson -Service 'svc-a' -Spans @{ 'http.request.method' = 'GET' }, @{ 'db.system' = 'redis' }),
            '{"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[{"attributes":[{"key":"db.system","value":{"stringValue":"mssql"}}]}]}]}]}'
        )
        try {
            (Get-DbSpanMatrix -Path $path).Count | Should Be 0
        } finally { Remove-Item -LiteralPath $path -Force }
    }
}

Describe 'Get-ExpectedDbClients' {
    It 'expects Npgsql only on modern frameworks' {
        (@(Get-ExpectedDbClients -Framework 'net48') -join '|') | Should Be 'SqlClient'
        (@(Get-ExpectedDbClients -Framework 'net8.0') -join '|') | Should Be 'SqlClient|Npgsql'
        (@(Get-ExpectedDbClients -Framework 'net10.0') -join '|') | Should Be 'SqlClient|Npgsql'
    }
}

Describe 'Compare-DbSpanMatrix' {
    $matrix = @{
        'svc-modern' = @{ 'SqlClient' = [pscustomobject]@{ Spans = 3; Params = $true }; 'Npgsql' = [pscustomobject]@{ Spans = 2; Params = $true } }
        'svc-legacy' = @{ 'SqlClient' = [pscustomobject]@{ Spans = 2; Params = $false } }
    }

    It 'passes when every service sent its expected clients' {
        $expectations = @(
            [pscustomobject]@{ Service = 'svc-modern'; Clients = @('SqlClient', 'Npgsql') },
            [pscustomobject]@{ Service = 'svc-legacy'; Clients = @('SqlClient') }
        )
        $rows = @(Compare-DbSpanMatrix -Matrix $matrix -Expectations $expectations)
        $rows.Count | Should Be 3
        @($rows | Where-Object Status -ne 'OK').Count | Should Be 0
        ($rows | Where-Object { $_.Service -eq 'svc-modern' -and $_.Client -eq 'Npgsql' }).Spans | Should Be 2
    }

    It 'reports a missing client and an absent service' {
        $expectations = @(
            [pscustomobject]@{ Service = 'svc-legacy'; Clients = @('SqlClient', 'Npgsql') },
            [pscustomobject]@{ Service = 'svc-absent'; Clients = @('SqlClient') }
        )
        $missing = @(Compare-DbSpanMatrix -Matrix $matrix -Expectations $expectations | Where-Object Status -eq 'MISSING')
        $missing.Count | Should Be 2
        ($missing | ForEach-Object { "$($_.Service)/$($_.Client)" }) -join '|' | Should Be 'svc-legacy/Npgsql|svc-absent/SqlClient'
        $missing[1].Spans | Should Be 0
    }
}
