using System;
using System.Collections.Generic;
using OpenTelemetry.Exporter;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class OtlpEnvironmentTests
    {
        // Injected rather than mutating process-wide OTEL_* variables, which other suites pick up
        // through the OtlpExporterOptions constructor.
        private static Func<string, string?> Env(params (string Name, string? Value)[] values)
        {
            var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in values)
                map[name] = value;

            return name => map.TryGetValue(name, out var value) ? value : null;
        }

        // The regression this exists for: the spec defines per-signal variables and gives them
        // precedence, so a deployment using only those must resolve rather than look unconfigured.
        // OtlpSignal is internal, so it cannot appear in a public test signature - hence one fact per
        // signal rather than a Theory.
        private static void AssertPerSignalVariableIsRead(OtlpSignal signal, string variable)
        {
            var resolved = OtlpEnvironment.ResolveEndpoint(
                null, signal, Env((variable, "http://collector:4318/v1/x")));

            Assert.Equal(new Uri("http://collector:4318/v1/x"), resolved);
        }

        [Fact]
        public void ResolveEndpoint_ReadsTracesVariable()
            => AssertPerSignalVariableIsRead(OtlpSignal.Traces, "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT");

        [Fact]
        public void ResolveEndpoint_ReadsMetricsVariable()
            => AssertPerSignalVariableIsRead(OtlpSignal.Metrics, "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT");

        [Fact]
        public void ResolveEndpoint_ReadsLogsVariable()
            => AssertPerSignalVariableIsRead(OtlpSignal.Logs, "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT");

        [Fact]
        public void ResolveEndpoint_PerSignalBeatsGeneric()
        {
            var resolved = OtlpEnvironment.ResolveEndpoint(null, OtlpSignal.Metrics, Env(
                ("OTEL_EXPORTER_OTLP_METRICS_ENDPOINT", "http://per-signal:4318"),
                ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://generic:4317")));

            Assert.Equal(new Uri("http://per-signal:4318"), resolved);
        }

        [Fact]
        public void ResolveEndpoint_OnlyTheOwnSignalVariableApplies()
        {
            var resolved = OtlpEnvironment.ResolveEndpoint(
                null, OtlpSignal.Traces, Env(("OTEL_EXPORTER_OTLP_METRICS_ENDPOINT", "http://metrics:4318")));

            Assert.Null(resolved);
        }

        [Fact]
        public void ResolveEndpoint_ConfigurationBeatsEverything()
        {
            var resolved = OtlpEnvironment.ResolveEndpoint(
                new Uri("http://configured:4318"), OtlpSignal.Traces, Env(
                    ("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "http://per-signal:4318"),
                    ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://generic:4317")));

            Assert.Equal(new Uri("http://configured:4318"), resolved);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("localhost:4317")]
        [InlineData("/v1/traces")]
        public void ResolveEndpoint_UnusableEnvironmentValue_ReturnsNull(string? value)
        {
            var resolved = OtlpEnvironment.ResolveEndpoint(
                null, OtlpSignal.Traces, Env(("OTEL_EXPORTER_OTLP_ENDPOINT", value)));

            Assert.Null(resolved);
        }

        [Fact]
        public void ResolveHeaders_PerSignalBeatsGeneric()
        {
            var resolved = OtlpEnvironment.ResolveHeaders(null, OtlpSignal.Logs, Env(
                ("OTEL_EXPORTER_OTLP_LOGS_HEADERS", "api-key=logs"),
                ("OTEL_EXPORTER_OTLP_HEADERS", "api-key=generic")));

            Assert.Equal("api-key=logs", resolved);
        }

        [Fact]
        public void ResolveHeaders_ConfigurationBeatsEnvironment()
        {
            var resolved = OtlpEnvironment.ResolveHeaders("api-key=configured", OtlpSignal.Logs,
                Env(("OTEL_EXPORTER_OTLP_HEADERS", "api-key=generic")));

            Assert.Equal("api-key=configured", resolved);
        }

        // The 401 regression, twice over: the api-key must survive coming from the environment. The
        // signal-less overload backs GetApiKey, which has no signal to scope to.
        [Fact]
        public void ResolveHeaders_WithoutSignal_FallsBackToGenericVariable()
        {
            var resolved = OtlpEnvironment.ResolveHeaders(
                null, Env(("OTEL_EXPORTER_OTLP_HEADERS", "api-key=from-env")));

            Assert.Equal("api-key=from-env", resolved);
        }

        [Fact]
        public void ResolveHeaders_WithoutSignal_IgnoresPerSignalVariables()
        {
            var resolved = OtlpEnvironment.ResolveHeaders(
                null, Env(("OTEL_EXPORTER_OTLP_LOGS_HEADERS", "api-key=logs")));

            Assert.Null(resolved);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void ResolveHeaders_BlankConfigurationFallsThrough(string configured)
        {
            var resolved = OtlpEnvironment.ResolveHeaders(configured, OtlpSignal.Logs,
                Env(("OTEL_EXPORTER_OTLP_HEADERS", "api-key=generic")));

            Assert.Equal("api-key=generic", resolved);
        }

        // Protocol resolution is modern-only: net48 is HttpProtobuf-only and the SDK marks
        // OtlpExportProtocol.Grpc obsolete there.
#if !NETFRAMEWORK

        // Grpc is the SDK default and the :4317 port; resolving to anything else silently retargets.
        [Fact]
        public void ResolveProtocol_NothingConfigured_DefaultsToGrpc()
        {
            Assert.Equal(OtlpExportProtocol.Grpc,
                OtlpEnvironment.ResolveProtocol(null, OtlpSignal.Traces, Env()));
        }

        [Fact]
        public void ResolveProtocol_PerSignalBeatsGeneric()
        {
            var resolved = OtlpEnvironment.ResolveProtocol(null, OtlpSignal.Traces, Env(
                ("OTEL_EXPORTER_OTLP_TRACES_PROTOCOL", "http/protobuf"),
                ("OTEL_EXPORTER_OTLP_PROTOCOL", "grpc")));

            Assert.Equal(OtlpExportProtocol.HttpProtobuf, resolved);
        }

        [Theory]
        [InlineData("http/protobuf", OtlpExportProtocol.HttpProtobuf)]
        [InlineData("HttpProtobuf", OtlpExportProtocol.HttpProtobuf)]
        [InlineData("grpc", OtlpExportProtocol.Grpc)]
        [InlineData("  GRPC  ", OtlpExportProtocol.Grpc)]
        public void ParseProtocol_AcceptsSpecSpellingsAndEnumNames(string value, OtlpExportProtocol expected)
        {
            Assert.Equal(expected, OtlpEnvironment.ParseProtocol(value));
        }

        // Enum.TryParse accepts numeric text, so without Enum.IsDefined "7" became
        // (OtlpExportProtocol)7 and only failed later when the SDK built the provider.
        [Theory]
        [InlineData("7")]
        [InlineData("-1")]
        [InlineData("nonsense")]
        [InlineData("")]
        public void ParseProtocol_RejectsUndefinedValues(string value)
        {
            Assert.Null(OtlpEnvironment.ParseProtocol(value));
        }
#endif

        private static TestOptions ProviderOptions(OpenTelemetryServiceProvider provider) =>
            new TestOptions { ServiceProvider = provider };

        private static string? Mismatch(OpenTelemetryServiceProvider provider, OtlpSignal signal, string endpoint) =>
            OtlpEnvironment.DescribeSignalEndpointMismatch(
                ProviderOptions(provider), signal, new Uri(endpoint));

        // The whole nr-data.net OTLP family, not just the US host - anchoring on otlp.nr-data.net alone
        // would warn on every EU, JP and FedRAMP tenant.
        [Theory]
        [InlineData("https://otlp.nr-data.net")]
        [InlineData("https://otlp.eu01.nr-data.net")]
        [InlineData("https://otlp.jp.nr-data.net")]
        [InlineData("https://gov-otlp.nr-data.net")]
        [InlineData("https://otlp.nr-data.net:4318/v1/logs")]
        public void NewRelicOtlpEndpoints_AreAccepted(string endpoint)
        {
            Assert.Null(Mismatch(OpenTelemetryServiceProvider.NewRelic, OtlpSignal.Logs, endpoint));
            Assert.Null(Mismatch(OpenTelemetryServiceProvider.NewRelic, OtlpSignal.Metrics, endpoint));
        }

        // A trace observer takes spans only, so logs or metrics aimed at one are discarded on arrival.
        private const string TraceObserver = "https://abc123.aws-us-east-1.tracing.edge.nr-data.net:4317";

        [Fact]
        public void NewRelic_TraceObserverEndpoint_IsReportedForLogs()
        {
            var detail = Mismatch(OpenTelemetryServiceProvider.NewRelic, OtlpSignal.Logs, TraceObserver);

            Assert.NotNull(detail);
            Assert.Contains("trace observer", detail!);
            Assert.Contains("Telemetry:LoggingEndpoint", detail);
        }

        [Fact]
        public void NewRelic_TraceObserverEndpoint_IsReportedForMetrics()
        {
            var detail = Mismatch(OpenTelemetryServiceProvider.NewRelic, OtlpSignal.Metrics, TraceObserver);

            Assert.NotNull(detail);
            Assert.Contains("trace observer", detail!);
            Assert.Contains("Telemetry:MetricsEndpoint", detail);
        }

        // Traces are the one signal a trace observer does serve.
        [Fact]
        public void NewRelic_TraceObserverEndpoint_IsFineForTraces()
        {
            Assert.Null(Mismatch(OpenTelemetryServiceProvider.NewRelic, OtlpSignal.Traces, TraceObserver));
        }

        // Exporting through a collector is normal; warning on every unrecognised host would be noise.
        [Theory]
        [InlineData("http://localhost:4318")]
        [InlineData("https://otel-collector.internal:4317")]
        public void UnrecognisedHosts_AreNotReported(string endpoint)
        {
            Assert.Null(Mismatch(OpenTelemetryServiceProvider.NewRelic, OtlpSignal.Logs, endpoint));
            Assert.Null(Mismatch(OpenTelemetryServiceProvider.Dynatrace, OtlpSignal.Logs, endpoint));
            Assert.Null(Mismatch(OpenTelemetryServiceProvider.Collector, OtlpSignal.Logs, endpoint));
        }

        // A collector forwards wherever it is configured to, so even a vendor-shaped endpoint that the
        // matching provider would reject is not evidence of misconfiguration.
        [Theory]
        [InlineData("http://localhost:4318")]
        [InlineData(TraceObserver)]
        [InlineData("https://abc12345.live.dynatrace.com/api/v2/metrics")]
        public void Collector_NeverReportsAMismatch(string endpoint)
        {
            Assert.Null(Mismatch(OpenTelemetryServiceProvider.Collector, OtlpSignal.Logs, endpoint));
            Assert.Null(Mismatch(OpenTelemetryServiceProvider.Collector, OtlpSignal.Metrics, endpoint));
        }

        // Omitting ServiceProvider must not opt a host into New Relic endpoint validation.
        [Fact]
        public void ProviderNotConfigured_TraceObserverEndpoint_IsNotReported()
        {
            Assert.Null(OtlpEnvironment.DescribeSignalEndpointMismatch(
                new TestOptions(), OtlpSignal.Logs, new Uri(TraceObserver)));
            Assert.NotNull(Mismatch(OpenTelemetryServiceProvider.NewRelic, OtlpSignal.Logs, TraceObserver));
        }

        // Dynatrace is path-shaped, not host-shaped: ActiveGate and Managed hosts are customer-specific,
        // so /api/v2/otlp is the only invariant.
        [Theory]
        [InlineData("https://abc12345.live.dynatrace.com/api/v2/otlp")]
        [InlineData("https://activegate.corp.local:9999/e/abc12345/api/v2/otlp")]
        [InlineData("https://dt.example.dynatrace.com/e/abc12345/api/v2/otlp/v1/logs")]
        public void DynatraceOtlpEndpoints_AreAccepted(string endpoint)
        {
            Assert.Null(Mismatch(OpenTelemetryServiceProvider.Dynatrace, OtlpSignal.Logs, endpoint));
        }

        [Fact]
        public void Dynatrace_HostWithoutOtlpApiPath_IsReported()
        {
            var detail = Mismatch(OpenTelemetryServiceProvider.Dynatrace, OtlpSignal.Metrics,
                "https://abc12345.live.dynatrace.com/api/v2/metrics");

            Assert.NotNull(detail);
            Assert.Contains("/api/v2/otlp", detail!);
        }

        [Theory]
        [InlineData("https://log-api.nr-data.net/log/v1")]
        [InlineData("https://metric-api.eu.nr-data.net")]
        public void NewRelic_NonOtlpHost_IsReportedWithTheExpectedHosts(string endpoint)
        {
            var detail = Mismatch(OpenTelemetryServiceProvider.NewRelic, OtlpSignal.Logs, endpoint);

            Assert.NotNull(detail);
            Assert.Contains("is not a New Relic OTLP endpoint", detail!);
            Assert.Contains("Telemetry:LoggingEndpoint", detail);
            Assert.DoesNotContain("trace observer", detail);
        }

        [Fact]
        public void WarnIfSignalEndpointMismatched_Mismatch_WritesDegraded()
        {
            var component = "comp-" + Guid.NewGuid().ToString("N");
            Assert.SkipUnless(GmoTelemetryEventSourceTests.EventSourceDispatches, "Mono EventSource does not dispatch to listeners");
            using var capture = new GmoTelemetryEventSourceTests.Capture();

            OtlpEnvironment.WarnIfSignalEndpointMismatched(
                ProviderOptions(OpenTelemetryServiceProvider.NewRelic), OtlpSignal.Metrics, TraceObserver, component);

            var e = Assert.Single(capture.ForComponent(component));
            Assert.Equal(4, e.EventId);
            Assert.Contains("trace observer", (string)e.Payload![1]!);
        }

        [Theory]
        [InlineData("http://localhost:4318")]
        [InlineData("not a uri")]
        [InlineData(null)]
        public void WarnIfSignalEndpointMismatched_UsableOrUnparsable_WritesNothing(string? endpoint)
        {
            var component = "comp-" + Guid.NewGuid().ToString("N");
            Assert.SkipUnless(GmoTelemetryEventSourceTests.EventSourceDispatches, "Mono EventSource does not dispatch to listeners");
            using var capture = new GmoTelemetryEventSourceTests.Capture();

            OtlpEnvironment.WarnIfSignalEndpointMismatched(
                ProviderOptions(OpenTelemetryServiceProvider.NewRelic), OtlpSignal.Logs, endpoint, component);

            Assert.Empty(capture.ForComponent(component));
        }

        [Fact]
        public void NullEndpoint_IsNotReported()
        {
            Assert.Null(OtlpEnvironment.DescribeSignalEndpointMismatch(
                ProviderOptions(OpenTelemetryServiceProvider.NewRelic), OtlpSignal.Logs, null));
        }

        [Fact]
        public void MissingEndpoint_NamesEveryKeyAndTheOptOut()
        {
            var ex = OtlpEnvironment.MissingEndpoint("Metrics", "Telemetry:MeterSourceNames to an empty array",
                "Telemetry:MetricsEndpoint", "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT");

            Assert.Contains("Metrics", ex.Message);
            Assert.Contains("Telemetry:MetricsEndpoint", ex.Message);
            Assert.Contains("OTEL_EXPORTER_OTLP_METRICS_ENDPOINT", ex.Message);
            Assert.Contains("Telemetry:MeterSourceNames", ex.Message);
        }
    }
}
