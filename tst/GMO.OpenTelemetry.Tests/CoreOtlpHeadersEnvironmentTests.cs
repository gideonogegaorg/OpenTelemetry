using OpenTelemetry.Exporter;
using Xunit;

#if !NETFRAMEWORK

namespace GMO.OpenTelemetry.Tests
{
    [CollectionDefinition(nameof(CoreOtlpHeadersEnvironment), DisableParallelization = true)]
    public class CoreOtlpHeadersEnvironment { }

    // The header variables are process-wide and read by both the library and OtlpExporterOptions, so
    // these tests clear them and run alone.
    [Collection(nameof(CoreOtlpHeadersEnvironment))]
    public sealed class CoreOtlpHeadersEnvironmentTests : IDisposable
    {
        private static readonly string[] HeaderVariables =
        [
            "OTEL_EXPORTER_OTLP_HEADERS",
            "OTEL_EXPORTER_OTLP_METRICS_HEADERS",
        ];

        private readonly Dictionary<string, string?> _saved = [];

        public CoreOtlpHeadersEnvironmentTests()
        {
            foreach (var name in HeaderVariables)
            {
                _saved[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        public void Dispose()
        {
            foreach (var pair in _saved)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }

        private static TestOptions MetricsOptions(string? headers) => new()
        {
            Enabled = true,
            HeadersOverride = headers,
            EnableGzipCompression = false,
            Otlp = new OtlpSettings { Protocol = OtlpExportProtocol.Grpc },
        };

        [Fact]
        public void ConfigureMetricsOtlpExporter_NoHeadersAnywhere_LeavesExporterHeadersUnset()
        {
            var exporter = new OtlpExporterOptions();

            OpenTelemetryExtensions.ConfigureMetricsOtlpExporter(
                exporter, null, MetricsOptions(null), new Uri("https://otlp.nr-data.net:4317"));

            Assert.True(string.IsNullOrEmpty(exporter.Headers));
        }

        [Fact]
        public void ConfigureMetricsOtlpExporter_MetricsHeaderVariable_IsApplied()
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_METRICS_HEADERS", "api-key=from-env");
            var exporter = new OtlpExporterOptions();

            OpenTelemetryExtensions.ConfigureMetricsOtlpExporter(
                exporter, null, MetricsOptions(null), new Uri("https://otlp.nr-data.net:4317"));

            Assert.Equal("api-key=from-env", exporter.Headers);
        }

        // OtlpExporterOptions pre-fills Headers from OTEL_EXPORTER_OTLP_HEADERS when constructed, so the
        // configured value must replace it rather than defer to it.
        [Fact]
        public void ConfigureMetricsOtlpExporter_GenericVariableAndConfiguredHeaders_ConfiguredWins()
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS", "api-key=generic");
            var exporter = new OtlpExporterOptions();

            OpenTelemetryExtensions.ConfigureMetricsOtlpExporter(
                exporter, null, MetricsOptions("api-key=configured"), new Uri("https://otlp.nr-data.net:4317"));

            Assert.Equal("api-key=configured", exporter.Headers);
        }

        [Fact]
        public void ConfigureMetricsOtlpExporter_GenericAndMetricsVariables_MetricsVariableWins()
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS", "api-key=generic");
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_METRICS_HEADERS", "api-key=metrics");
            var exporter = new OtlpExporterOptions();

            OpenTelemetryExtensions.ConfigureMetricsOtlpExporter(
                exporter, null, MetricsOptions(null), new Uri("https://otlp.nr-data.net:4317"));

            Assert.Equal("api-key=metrics", exporter.Headers);
        }

        [Fact]
        public void ConfigureMetricsOtlpExporter_NothingResolved_KeepsHeadersTheCallerSet()
        {
            var exporter = new OtlpExporterOptions { Headers = "api-key=caller" };

            OpenTelemetryExtensions.ConfigureMetricsOtlpExporter(
                exporter, null, MetricsOptions(null), new Uri("https://otlp.nr-data.net:4317"));

            Assert.Equal("api-key=caller", exporter.Headers);
        }
    }
}

#endif
