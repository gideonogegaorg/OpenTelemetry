#if !NETFRAMEWORK
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using GMO.OpenTelemetry.Npgsql;
using Npgsql;
using NpgsqlTypes;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class NpgsqlInstrumentationExtensionsTests : IDisposable
    {
        private readonly ActivitySource _source = new("Test.Npgsql");
        private readonly ActivityListener _listener;

        public NpgsqlInstrumentationExtensionsTests()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = s => s.Name == _source.Name,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose()
        {
            _listener.Dispose();
            _source.Dispose();
            GC.SuppressFinalize(this);
        }

        private sealed class CollectingProcessor : BaseProcessor<Activity>
        {
            public ConcurrentBag<string> Names { get; } = new();

            public override void OnEnd(Activity data) => Names.Add(data.DisplayName);
        }

        private sealed class CollectingMetricExporter : BaseExporter<Metric>
        {
            public ConcurrentBag<string> Meters { get; } = new();

            public override ExportResult Export(in Batch<Metric> batch)
            {
                foreach (var metric in batch)
                    Meters.Add(metric.MeterName);
                return ExportResult.Success;
            }
        }

        // Npgsql exposes no public read-back of tracing options; these private field names are pinned to Npgsql 10.
        private static T? GetTracingField<T>(NpgsqlTracingOptionsBuilder options, string field) where T : class =>
            typeof(NpgsqlTracingOptionsBuilder)
                .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(options) as T;

        private static NpgsqlTracingOptionsBuilder CaptureTracingOptions(Action<NpgsqlDataSourceBuilder> configure)
        {
            NpgsqlTracingOptionsBuilder? captured = null;
            var builder = new NpgsqlDataSourceBuilder("Host=localhost;Database=test");
            configure(builder);
            builder.ConfigureTracing(t => captured = t);
            using var dataSource = builder.Build();
            return captured!;
        }

        [Fact]
        public void ActivitySourceAndMeterNames_MatchNpgsql()
        {
            Assert.Equal("Npgsql", NpgsqlInstrumentationExtensions.ActivitySourceName);
            Assert.Equal("Npgsql", NpgsqlInstrumentationExtensions.MeterName);
        }

        [Fact]
        public void AddNpgsqlInstrumentationWithEnrichment_SubscribesToNpgsqlActivitySource()
        {
            var processor = new CollectingProcessor();
            using var provider = Sdk.CreateTracerProviderBuilder()
                .AddNpgsqlInstrumentationWithEnrichment()
                .AddProcessor(processor)
                .Build();

            using (var npgsqlSource = new ActivitySource(NpgsqlInstrumentationExtensions.ActivitySourceName))
            using (npgsqlSource.StartActivity("SELECT npgsql")) { }
            using (var otherSource = new ActivitySource("Test.Npgsql.Unrelated"))
            using (otherSource.StartActivity("unrelated")) { }

            Assert.Contains("SELECT npgsql", processor.Names);
            Assert.DoesNotContain("unrelated", processor.Names);
        }

        [Fact]
        public void AddNpgsqlMetrics_SubscribesToNpgsqlMeter()
        {
            var exporter = new CollectingMetricExporter();
            using var meter = new Meter(NpgsqlInstrumentationExtensions.MeterName);
            using var other = new Meter("Test.Npgsql.UnrelatedMeter");
            using var provider = Sdk.CreateMeterProviderBuilder()
                .AddNpgsqlMetrics()
                .AddReader(new BaseExportingMetricReader(exporter))
                .Build();

            meter.CreateCounter<long>("test.npgsql.counter").Add(1);
            other.CreateCounter<long>("test.unrelated.counter").Add(1);
            provider.ForceFlush();

            Assert.Contains(NpgsqlInstrumentationExtensions.MeterName, exporter.Meters);
            Assert.DoesNotContain("Test.Npgsql.UnrelatedMeter", exporter.Meters);
        }

        [Fact]
        public void AddNpgsqlInstrumentationWithEnrichment_NullBuilder_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => ((TracerProviderBuilder)null!).AddNpgsqlInstrumentationWithEnrichment());
        }

        [Fact]
        public void AddNpgsqlMetrics_NullBuilder_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => ((MeterProviderBuilder)null!).AddNpgsqlMetrics());
        }

        [Fact]
        public void ConfigureTracingWithEnrichment_NullDataSourceBuilder_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => ((NpgsqlDataSourceBuilder)null!).ConfigureTracingWithEnrichment());
        }

        [Fact]
        public void ConfigureTracingWithEnrichment_NullSlimDataSourceBuilder_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => ((NpgsqlSlimDataSourceBuilder)null!).ConfigureTracingWithEnrichment());
        }

        [Fact]
        public void UseEnrichment_NullOptions_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => ((NpgsqlTracingOptionsBuilder)null!).UseEnrichment());
        }

        [Fact]
        public void ConfigureTracingWithEnrichment_TextEnrichmentOff_InstallsNoCallbacks()
        {
            var options = CaptureTracingOptions(b => b.ConfigureTracingWithEnrichment(enableTextEnrichment: false));

            Assert.Null(GetTracingField<Action<Activity, NpgsqlCommand>>(options, "_commandEnrichmentCallback"));
            Assert.Null(GetTracingField<Action<Activity, NpgsqlBatch>>(options, "_batchEnrichmentCallback"));
            Assert.Null(GetTracingField<Func<NpgsqlCommand, bool>>(options, "_commandFilter"));
        }

        [Fact]
        public void ConfigureTracingWithEnrichment_TextEnrichmentOn_InstallsCommandAndBatchCallbacks()
        {
            var options = CaptureTracingOptions(b => b.ConfigureTracingWithEnrichment(enableTextEnrichment: true));

            Assert.NotNull(GetTracingField<Action<Activity, NpgsqlCommand>>(options, "_commandEnrichmentCallback"));
            Assert.NotNull(GetTracingField<Action<Activity, NpgsqlBatch>>(options, "_batchEnrichmentCallback"));
        }

        [Fact]
        public void ConfigureTracingWithEnrichment_CommandFilter_IsInstalled()
        {
            Func<NpgsqlCommand, bool> filter = c => !c.CommandText.StartsWith("SELECT 1", StringComparison.Ordinal);
            var options = CaptureTracingOptions(b => b.ConfigureTracingWithEnrichment(commandFilter: filter));

            Assert.Same(filter, GetTracingField<Func<NpgsqlCommand, bool>>(options, "_commandFilter"));
        }

        [Fact]
        public void ConfigureTracingWithEnrichment_SlimBuilder_ReturnsSameBuilder()
        {
            var builder = new NpgsqlSlimDataSourceBuilder("Host=localhost;Database=test");
            Assert.Same(builder, builder.ConfigureTracingWithEnrichment(enableTextEnrichment: true));
        }

        [Fact]
        public void CommandEnrichmentCallback_SetsSharedEnrichmentTags()
        {
            var options = CaptureTracingOptions(b => b.ConfigureTracingWithEnrichment(enableTextEnrichment: true));
            var callback = GetTracingField<Action<Activity, NpgsqlCommand>>(options, "_commandEnrichmentCallback")!;

            using var activity = _source.StartActivity("Test")!;
            using var cmd = new NpgsqlCommand("SELECT * FROM users WHERE id = @id") { CommandTimeout = 45 };
            cmd.Parameters.AddWithValue("id", 7);
            cmd.Parameters.Add(new NpgsqlParameter("missing", NpgsqlDbType.Text) { Value = DBNull.Value });
            callback(activity, cmd);

            Assert.Equal(45, activity.GetTagItem("db.commandTimeout"));
            Assert.Equal("Text", activity.GetTagItem("db.commandType"));
            Assert.Equal("{\"id\":7}", activity.GetTagItem("db.params"));
            Assert.Null(activity.GetTagItem("db.transaction.isolation_level"));
        }

        [Fact]
        public void CommandEnrichmentCallback_PositionalParameters_SetsJsonArray()
        {
            var options = CaptureTracingOptions(b => b.ConfigureTracingWithEnrichment(enableTextEnrichment: true));
            var callback = GetTracingField<Action<Activity, NpgsqlCommand>>(options, "_commandEnrichmentCallback")!;

            using var activity = _source.StartActivity("Test")!;
            using var cmd = new NpgsqlCommand("SELECT $1, $2");
            cmd.Parameters.Add(new NpgsqlParameter { Value = 42 });
            cmd.Parameters.Add(new NpgsqlParameter { Value = "smoke" });
            callback(activity, cmd);

            Assert.Equal("[42,\"smoke\"]", activity.GetTagItem("db.params"));
        }

        [Fact]
        public void BatchEnrichmentCallback_SetsTimeoutSizeAndPerCommandParams()
        {
            var options = CaptureTracingOptions(b => b.ConfigureTracingWithEnrichment(enableTextEnrichment: true));
            var callback = GetTracingField<Action<Activity, NpgsqlBatch>>(options, "_batchEnrichmentCallback")!;

            using var activity = _source.StartActivity("Test")!;
            using var batch = new NpgsqlBatch { Timeout = 15 };
            var insert = new NpgsqlBatchCommand("INSERT INTO t (name) VALUES (@name)");
            insert.Parameters.AddWithValue("name", "alice");
            batch.BatchCommands.Add(insert);
            batch.BatchCommands.Add(new NpgsqlBatchCommand("SELECT 1"));
            callback(activity, batch);

            Assert.Equal(15, activity.GetTagItem("db.commandTimeout"));
            Assert.Equal(2, activity.GetTagItem("db.operation.batch.size"));
            Assert.Equal("[{\"name\":\"alice\"},{}]", activity.GetTagItem("db.params"));
        }

        [Fact]
        public void BatchEnrichmentCallback_NoParameters_NoDbParamsTag()
        {
            var options = CaptureTracingOptions(b => b.ConfigureTracingWithEnrichment(enableTextEnrichment: true));
            var callback = GetTracingField<Action<Activity, NpgsqlBatch>>(options, "_batchEnrichmentCallback")!;

            using var activity = _source.StartActivity("Test")!;
            using var batch = new NpgsqlBatch();
            batch.BatchCommands.Add(new NpgsqlBatchCommand("SELECT 1"));
            callback(activity, batch);

            Assert.Equal(1, activity.GetTagItem("db.operation.batch.size"));
            Assert.Null(activity.GetTagItem("db.params"));
        }
    }
}
#endif
