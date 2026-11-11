using System;
using System.Data.Common;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
#if !NETFRAMEWORK
using Npgsql;
#endif

namespace GMO.OpenTelemetry.TestApps.Common
{
    /// <summary>
    /// Runs a deterministic smoke catalog that exercises core OpenTelemetry package surfaces.
    /// </summary>
    public sealed class TelemetryScenarioRunner
    {
        private readonly DemoOpenTelemetryOptions _options;
        private readonly ActivitySource _activitySource;
        private readonly ICorrelationIdService _correlationIdService;
        private readonly IAttributeEnricher _attributeEnricher;
        private readonly IActivityLogger? _activityLogger;
        private readonly Action<string>? _writeLog;
        private readonly Action<string>? _writeInfo;
#if !NETFRAMEWORK
        private readonly NpgsqlDataSource? _postgresDataSource;
#endif
        private System.Diagnostics.Metrics.Meter? _smokeMeter;

        public TelemetryScenarioRunner(
            DemoOpenTelemetryOptions options,
            ActivitySource activitySource,
            ICorrelationIdService correlationIdService,
            IAttributeEnricher attributeEnricher,
            IActivityLogger? activityLogger = null,
            Action<string>? writeLog = null,
            Action<string>? writeInfo = null
#if !NETFRAMEWORK
            , NpgsqlDataSource? postgresDataSource = null
#endif
            )
        {
            _options = options;
            _activitySource = activitySource;
            _correlationIdService = correlationIdService;
            _attributeEnricher = attributeEnricher;
            _activityLogger = activityLogger;
            _writeLog = writeLog;
            _writeInfo = writeInfo;
#if !NETFRAMEWORK
            _postgresDataSource = postgresDataSource;
#endif
        }

        public async Task<ScenarioResult> RunAsync(CancellationToken cancellationToken = default)
        {
            var result = new ScenarioResult();
            try
            {
                RunCorrelationAndEnrichment(result);
                RunActivityWrappers(result);
                await RunHttpAsync(result, cancellationToken).ConfigureAwait(false);
                RunLogging(result);
                RunOversizedSpan(result);
                RunCounterAndHistogram(result);
                await RunSqlClientAsync(result, cancellationToken).ConfigureAwait(false);
                await RunPostgresAsync(result, cancellationToken).ConfigureAwait(false);
                await WaitForMetricsFlushAsync(result, cancellationToken).ConfigureAwait(false);
                ApplyScenarioOutcome(result);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = ex.ToString();
                _writeInfo?.Invoke($"Scenario failure: {ex}");
            }

            return result;
        }

        private void ApplyScenarioOutcome(ScenarioResult result)
        {
            if (IsHttpFailure(result)
                || IsDbFailure(_options.SqlConnectionString, result.SqlStatus)
                || IsDbFailure(_options.PostgresConnectionString, result.PostgresStatus))
            {
                result.Success = false;
                result.Message = $"Scenario completed with failures (Http={result.HttpStatus}, Sql={result.SqlStatus}, Postgres={result.PostgresStatus}).";
                _writeInfo?.Invoke(result.Message);
                return;
            }

            result.Success = true;
            result.Message = "All scenarios completed.";
        }

        private bool IsHttpFailure(ScenarioResult result)
        {
            if (!_options.EnableHttpInstrumentation)
                return false;

            var status = result.HttpStatus;
            if (string.IsNullOrWhiteSpace(status) || status == "skipped")
                return false;

            if (status!.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
                return true;

            return !(status.StartsWith("2", StringComparison.Ordinal) && status.Length == 3);
        }

        private static bool IsDbFailure(string? connectionString, string? status) =>
            !string.IsNullOrWhiteSpace(connectionString)
            && !string.IsNullOrWhiteSpace(status)
            && status!.StartsWith("error:", StringComparison.OrdinalIgnoreCase);

        private void RunCorrelationAndEnrichment(ScenarioResult result)
        {
            var correlationId = _correlationIdService.GenerateCorrelationId();
            _correlationIdService.SetCorrelationId(correlationId);
            var roundTrip = _correlationIdService.GetCorrelationId();
            result.CorrelationId = roundTrip;
            _writeInfo?.Invoke($"CorrelationId={roundTrip}");

            var enriched = _attributeEnricher.Enrich(null);
            result.EnrichmentCount = enriched?.Count ?? 0;
        }

        private void RunActivityWrappers(ScenarioResult result)
        {
            using (var root = new RootActivityWrapper("TestApp.Root", _activitySource, new ActivityOptions { ForceNewDetachedRoot = true, Log = _activityLogger }))
            {
                root.SetTag("testapp.scenario", "root");
                root.AddEvent("RootStarted");

                using var child = new ActivityWrapper("TestApp.Child", _activitySource, new ActivityOptions { Log = _activityLogger });
                child.SetTag("testapp.user", new { id = 42, name = "smoke" });
                child.SetTag("testapp.primitive", 123);
                child.AddEvent("ChildProcessing");
                child.SetStatus(ActivityStatusCode.Ok);

                var currentRoot = Activity.Current?.GetRoot();
                result.RootActivityName = currentRoot?.OperationName;

                try
                {
                    throw new InvalidOperationException("Intentional smoke exception");
                }
                catch (Exception ex)
                {
                    child.RecordException(ex);
                    result.RecordedException = true;
                }
            }

            using (var nestedRoot = new RootActivityWrapper("TestApp.NestedRoot", _activitySource, new ActivityOptions { ForceNewDetachedRoot = false, Log = _activityLogger }))
            {
                nestedRoot.SetTag("testapp.scenario", "nested-root");
            }

            result.ActivityScenariosRan = true;
        }

        private async Task RunHttpAsync(ScenarioResult result, CancellationToken cancellationToken)
        {
            if (!_options.EnableHttpInstrumentation)
            {
                result.HttpStatus = "skipped";
                return;
            }

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var response = await client.GetAsync("https://example.com/", cancellationToken).ConfigureAwait(false);
                result.HttpStatus = ((int)response.StatusCode).ToString();
                _writeInfo?.Invoke($"HTTP GET example.com => {response.StatusCode}");
            }
            catch (Exception ex)
            {
                result.HttpStatus = $"error:{ex.GetType().Name}";
                _writeInfo?.Invoke($"HTTP scenario error: {ex.Message}");
            }
        }

        private void RunLogging(ScenarioResult result)
        {
            _writeLog?.Invoke($"TestApp normal log from {_options.ServiceName}");

            var max = Math.Max(256, Math.Min(_options.MaxLogMessageLength, 4096));
            var oversized = new string('X', max + 512);
            _writeLog?.Invoke($"TestApp oversized log chunk trigger: {oversized}");
            result.LoggedOversizedMessage = true;
        }

        private void RunOversizedSpan(ScenarioResult result)
        {
            using var wrapper = new ActivityWrapper("TestApp.Overflow", _activitySource, new ActivityOptions { Log = _activityLogger });
            var length = Math.Max(512, _options.MaxAttributeValueLength + 128);
            wrapper.SetTag("db.statement", new string('S', length));
            wrapper.SetTag("testapp.overflow", "true");
            result.OversizedSpanAttribute = true;
        }

        private async Task RunSqlClientAsync(ScenarioResult result, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_options.SqlConnectionString))
            {
                result.SqlStatus = "skipped";
                _writeInfo?.Invoke($"SqlClient scenario => {result.SqlStatus}");
                return;
            }

            try
            {
#if NETFRAMEWORK
                using var connection = new System.Data.SqlClient.SqlConnection(_options.SqlConnectionString);
#else
                await using var connection = new Microsoft.Data.SqlClient.SqlConnection(_options.SqlConnectionString);
#endif
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                using (var query = connection.CreateCommand())
                {
                    query.CommandText = "SELECT @id AS Id, @name AS Name";
                    AddParameter(query, "@id", 42);
                    AddParameter(query, "@name", "smoke");
                    await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                }

                using var batch = connection.CreateCommand();
                batch.CommandText = "CREATE TABLE #smoke (Id INT NOT NULL, Name NVARCHAR(50) NOT NULL); "
                    + "INSERT INTO #smoke (Id, Name) VALUES (@id, @name); "
                    + "SELECT COUNT(*) FROM #smoke;";
                AddParameter(batch, "@id", 7);
                AddParameter(batch, "@name", "batch");
                var count = await batch.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                result.SqlStatus = $"ok:{count}";
            }
            catch (Exception ex)
            {
                result.SqlStatus = $"error:{ex.GetType().Name}";
                _writeInfo?.Invoke($"SQL scenario error: {ex.Message}");
            }

            _writeInfo?.Invoke($"SqlClient scenario => {result.SqlStatus}");
        }

#if NETFRAMEWORK
        private Task RunPostgresAsync(ScenarioResult result, CancellationToken cancellationToken)
        {
            result.PostgresStatus = "skipped:unsupported-framework";
            _writeInfo?.Invoke($"Postgres scenario => {result.PostgresStatus}");
            return Task.CompletedTask;
        }
#else
        private async Task RunPostgresAsync(ScenarioResult result, CancellationToken cancellationToken)
        {
            if (_postgresDataSource == null)
            {
                result.PostgresStatus = "skipped";
                _writeInfo?.Invoke($"Postgres scenario => {result.PostgresStatus}");
                return;
            }

            try
            {
                await using var connection = await _postgresDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using (var query = connection.CreateCommand())
                {
                    query.CommandText = "SELECT @id AS id, @name AS name";
                    AddParameter(query, "id", 42);
                    AddParameter(query, "name", "smoke");
                    await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (var positional = connection.CreateCommand())
                {
                    positional.CommandText = "SELECT $1 AS id, $2 AS name";
                    positional.Parameters.Add(new NpgsqlParameter { Value = 42 });
                    positional.Parameters.Add(new NpgsqlParameter { Value = "smoke" });
                    await positional.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                }

                await using var batch = connection.CreateBatch();
                batch.BatchCommands.Add(new NpgsqlBatchCommand("CREATE TEMP TABLE smoke (id INT NOT NULL, name TEXT NOT NULL)"));
                var insert = new NpgsqlBatchCommand("INSERT INTO smoke (id, name) VALUES (@id, @name)");
                insert.Parameters.AddWithValue("id", 7);
                insert.Parameters.AddWithValue("name", "batch");
                batch.BatchCommands.Add(insert);
                batch.BatchCommands.Add(new NpgsqlBatchCommand("SELECT COUNT(*) FROM smoke"));
                var count = await batch.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                result.PostgresStatus = $"ok:{count}";
            }
            catch (Exception ex)
            {
                result.PostgresStatus = $"error:{ex.GetType().Name}";
                _writeInfo?.Invoke($"Postgres scenario error: {ex.Message}");
            }

            _writeInfo?.Invoke($"Postgres scenario => {result.PostgresStatus}");
        }
#endif

        private static void AddParameter(DbCommand command, string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        // CustomMetrics only ships a gauge, and temporality does not apply to gauges - so without a
        // counter and a histogram here the smoke run cannot tell delta from cumulative, which is the
        // difference between Dynatrace ingesting these and discarding them.
        private void RunCounterAndHistogram(ScenarioResult result)
        {
            try
            {
                // Deliberately not disposed: disposing a Meter unpublishes its instruments, and the
                // MeterProvider's final collect happens at host shutdown, after this returns.
                _smokeMeter ??= new System.Diagnostics.Metrics.Meter(_options.ServiceName, _options.Version);
                var counter = _smokeMeter.CreateCounter<long>("testapp.smoke.count");
                var histogram = _smokeMeter.CreateHistogram<double>("testapp.smoke.duration");

                for (var i = 0; i < 3; i++)
                {
                    counter.Add(1);
                    histogram.Record(1.5 * (i + 1));
                }

                result.CounterAndHistogramRecorded = true;
            }
            catch (Exception ex)
            {
                _writeInfo?.Invoke($"Counter/histogram scenario error: {ex.Message}");
            }
        }

        private async Task WaitForMetricsFlushAsync(ScenarioResult result, CancellationToken cancellationToken)
        {
            // Allow runtime/process/CustomMetrics export batch to flush toward New Relic.
            var delayMs = Math.Max(5000, Math.Min(_options.MetricsExportDelayMs + 2000, 20000));
            _writeInfo?.Invoke($"Waiting {delayMs}ms for metrics flush...");
            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            result.MetricsWaitMs = delayMs;
        }
    }

    public sealed class ScenarioResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? CorrelationId { get; set; }
        public int EnrichmentCount { get; set; }
        public string? RootActivityName { get; set; }
        public bool RecordedException { get; set; }
        public bool ActivityScenariosRan { get; set; }
        public bool CounterAndHistogramRecorded { get; set; }
        public string? HttpStatus { get; set; }
        public bool LoggedOversizedMessage { get; set; }
        public bool OversizedSpanAttribute { get; set; }
        public string? SqlStatus { get; set; }
        public string? PostgresStatus { get; set; }
        public int MetricsWaitMs { get; set; }
    }
}
