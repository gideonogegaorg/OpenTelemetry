using Microsoft.Extensions.DependencyInjection;
using Moq;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

#if !NETFRAMEWORK

namespace GMO.OpenTelemetry.Tests
{
    // Extends TestOptions to allow GetOtlpEndpoint to return null for testing
    internal class NullEndpointOptions : TestOptions
    {
        public override Uri? GetOtlpEndpoint() => null;
    }

    public class OpenTelemetryExtensionsTests
    {
        private static readonly string[] TestMeters = new[] { "TestMeter" };

        private static TestOptions BuildOptions(bool enabled, bool http = false, bool console = false, Uri? metricsEndpoint = null, string[]? meters = null)
        {
            return new TestOptions
            {
                Enabled = enabled,
                EnableHttpInstrumentation = http,
                EnableConsole = console,
                HeadersOverride = "api-key=test-key",
                Otlp = new OtlpSettings { Endpoint = new Uri("http://localhost:4317"), Headers = "api-key=test-key" },
                MetricsEndpoint = metricsEndpoint,
                MeterSourceNames = meters ?? new[] { "*" },
            };
        }

        [Fact]
        public void AddGmoOpenTelemetry_Disabled_ReturnsBuilder()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var result = services.AddGmoOpenTelemetry(BuildOptions(false));
            Assert.NotNull(result);
        }

        [Fact]
        public void AddGmoOpenTelemetry_Enabled_RegistersServices()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ICorrelationIdService, CorrelationIdService>();
            var opts = BuildOptions(true);
            services.AddSingleton<IOpenTelemetryOptions>(opts);

            var result = services.AddGmoOpenTelemetry(opts);

            Assert.NotNull(result);
            var provider = services.BuildServiceProvider();
            Assert.NotNull(provider.GetService<TruncatingSpanProcessor>());
            Assert.NotNull(provider.GetService<IAttributeEnricher>());
        }

        [Fact]
        public void AddGmoOpenTelemetry_EnabledWithoutOtlp_Throws()
        {
            var opts = BuildOptions(true);
            opts.Otlp = null!;

            var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddGmoOpenTelemetry(opts));

            Assert.Contains("OtlpSettings", ex.Message);
        }

        [Fact]
        public void AddGmoOpenTelemetry_AdditionalMetrics_ConfiguresTheMeterProvider()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var opts = BuildOptions(true, meters: TestMeters);
            var configured = false;

            services.AddGmoOpenTelemetry(opts, additionalMetrics: _ => configured = true);
            using var provider = services.BuildServiceProvider();
            provider.GetRequiredService<MeterProvider>();

            Assert.True(configured);
        }

        // Options need not derive from OpenTelemetryOptionsBase; only that type can report malformed values.
        private static Mock<IOpenTelemetryOptions> InterfaceOnlyOptions()
        {
            var mock = new Mock<IOpenTelemetryOptions>();
            mock.SetupGet(o => o.Enabled).Returns(true);
            mock.SetupGet(o => o.TraceSourceNames).Returns(new[] { "Test" });
            mock.SetupGet(o => o.MeterSourceNames).Returns(TestMeters);
            mock.SetupGet(o => o.Otlp).Returns(new OtlpSettings());
            mock.Setup(o => o.GetOtlpEndpoint()).Returns(new Uri("http://localhost:4318"));
            mock.SetupGet(o => o.MetricsEndpoint).Returns(new Uri("http://localhost:4318"));
            return mock;
        }

        [Fact]
        public void WithGmoTracing_InterfaceOnlyOptions_ConfiguresTracing()
        {
            var builder = new ServiceCollection().AddOpenTelemetry();

            Assert.Same(builder, builder.WithGmoTracing(InterfaceOnlyOptions().Object));
        }

        [Fact]
        public void AddGmoMetrics_InterfaceOnlyOptions_ConfiguresMetrics()
        {
            var builder = Sdk.CreateMeterProviderBuilder();

            Assert.Same(builder, builder.AddGmoMetrics(InterfaceOnlyOptions().Object));
        }

        [Fact]
        public void AddGmoOpenTelemetry_WithHttpInstrumentation_DoesNotThrow()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ICorrelationIdService, CorrelationIdService>();
            var opts = BuildOptions(true, http: true);
            services.AddSingleton<IOpenTelemetryOptions>(opts);
            var result = services.AddGmoOpenTelemetry(opts);
            Assert.NotNull(result);
        }

        [Fact]
        public void WithGmoTracing_NoTraceSourceNames_ReturnsBuilderUnchanged()
        {
            var services = new ServiceCollection();
            var opts = BuildOptions(true);
            opts.TraceSourceNames = Array.Empty<string>();
            var builder = services.AddOpenTelemetry();
            var result = builder.WithGmoTracing(opts);
            Assert.Same(builder, result);
        }

        [Fact]
        public void WithGmoTracing_WithSources_ConfiguresTracing()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ICorrelationIdService, CorrelationIdService>();
            var opts = BuildOptions(true, console: true);
            services.AddSingleton<IOpenTelemetryOptions>(opts);
            services.AddSingleton<TruncatingSpanProcessor>();
            services.AddSingleton<CorrelationIdProcessor>();
            var builder = services.AddOpenTelemetry();
            var result = builder.WithGmoTracing(opts);
            Assert.NotNull(result);
        }

        [Fact]
        public void WithGmoTracing_WithHttpAndConsole_ConfiguresAllBranches()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ICorrelationIdService, CorrelationIdService>();
            var opts = BuildOptions(true, http: true, console: true);
            services.AddSingleton<IOpenTelemetryOptions>(opts);
            services.AddSingleton<TruncatingSpanProcessor>();
            services.AddSingleton<CorrelationIdProcessor>();
            var builder = services.AddOpenTelemetry();
            var result = builder.WithGmoTracing(opts);
            Assert.NotNull(result);
        }

        [Fact]
        public void AddGmoOpenTelemetry_Enabled_OtlpConfigLambdaExecuted()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ICorrelationIdService, CorrelationIdService>();
            var opts = BuildOptions(true, http: true);
            services.AddSingleton<IOpenTelemetryOptions>(opts);
            var result = services.AddGmoOpenTelemetry(opts);
            Assert.NotNull(result);
            // Build the provider to trigger Configure<OtlpExporterOptions> resolution
            using var provider = services.BuildServiceProvider();
            var otlpOpts = provider.GetService<Microsoft.Extensions.Options.IOptions<OtlpExporterOptions>>();
            Assert.NotNull(otlpOpts);
        }

        [Fact]
        public void AddMetrics_Disabled_DoesNotRegisterHostedService()
        {
            var services = new ServiceCollection();
            var builder = services.AddOpenTelemetry();
            services.AddMetrics(builder, BuildOptions(false), _ => { });
            // Disabled — CustomMetrics hosted service must not be registered
            var provider = services.BuildServiceProvider();
            var hostedServices = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>();
            Assert.DoesNotContain(hostedServices, s => s is CustomMetrics);
        }

        [Fact]
        public void AddMetrics_EnabledNoMeterSources_DoesNotRegisterHostedService()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var opts = BuildOptions(true, meters: Array.Empty<string>());
            var builder = services.AddOpenTelemetry();
            services.AddMetrics(builder, opts, _ => { });
            var provider = services.BuildServiceProvider();
            var hostedServices = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>();
            Assert.DoesNotContain(hostedServices, s => s is CustomMetrics);
        }

        [Fact]
        public void AddMetrics_EnabledWithMeters_RegistersCustomMetricsHostedService()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var opts = BuildOptions(true, metricsEndpoint: new Uri("http://localhost:4317"), meters: TestMeters);
            var builder = services.AddOpenTelemetry();
            services.AddMetrics(builder, opts, _ => { });
            var provider = services.BuildServiceProvider();
            var hostedServices = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>();
            Assert.Contains(hostedServices, s => s is CustomMetrics);
        }

        [Fact]
        public void WithMetrics_Disabled_ReturnsEarly()
        {
            var services = new ServiceCollection();
            var builder = services.AddOpenTelemetry();
            var exception = Record.Exception(() => builder.WithMetrics(BuildOptions(false), _ => { }));
            Assert.Null(exception);
        }

        [Fact]
        public void WithMetrics_NoMeterSources_ReturnsEarly()
        {
            var services = new ServiceCollection();
            var opts = BuildOptions(true, meters: Array.Empty<string>());
            var builder = services.AddOpenTelemetry();
            var exception = Record.Exception(() => builder.WithMetrics(opts, _ => { }));
            Assert.Null(exception);
        }

        // Metrics enabled with no endpoint used to return silently, so the meter provider was never
        // registered and nobody found out until the dashboards were empty.
        [Fact]
        public void WithMetrics_NullEndpoint_Throws()
        {
            var services = new ServiceCollection();
            var opts = new NullEndpointOptions
            {
                Enabled = true,
                MeterSourceNames = new[] { "TestMeter" },
                MetricsEndpoint = null,
            };
            var builder = services.AddOpenTelemetry();

            var ex = Assert.Throws<InvalidOperationException>(() => builder.WithMetrics(opts, _ => { }));

            Assert.Contains("Telemetry:MetricsEndpoint", ex.Message);
            Assert.Contains("Telemetry:MeterSourceNames", ex.Message);
        }

        [Fact]
        public void WithMetrics_NullEndpoint_Disabled_DoesNotThrow()
        {
            var services = new ServiceCollection();
            var opts = new NullEndpointOptions
            {
                Enabled = false,
                MeterSourceNames = new[] { "TestMeter" },
            };
            var builder = services.AddOpenTelemetry();

            Assert.Null(Record.Exception(() => builder.WithMetrics(opts, _ => { })));
        }

        // The opt-out must keep working: a logs-only service should not be told metrics are broken.
        [Fact]
        public void WithMetrics_NullEndpoint_NoMeterSources_DoesNotThrow()
        {
            var services = new ServiceCollection();
            var opts = new NullEndpointOptions
            {
                Enabled = true,
                MeterSourceNames = Array.Empty<string>(),
            };
            var builder = services.AddOpenTelemetry();

            Assert.Null(Record.Exception(() => builder.WithMetrics(opts, _ => { })));
        }

        [Fact]
        public void WithGmoTracing_NullEndpoint_Throws()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var opts = BuildOptions(true);
            opts.Otlp.Endpoint = null;
            var builder = services.AddOpenTelemetry();

            var ex = Assert.Throws<InvalidOperationException>(() => builder.WithGmoTracing(opts));

            Assert.Contains("Telemetry:Otlp:Endpoint", ex.Message);
            Assert.Contains("Telemetry:TraceSourceNames", ex.Message);
        }

        [Fact]
        public void WithGmoTracing_NullEndpoint_NoTraceSources_DoesNotThrow()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var opts = BuildOptions(true);
            opts.Otlp.Endpoint = null;
            opts.TraceSourceNames = Array.Empty<string>();
            var builder = services.AddOpenTelemetry();

            Assert.Null(Record.Exception(() => builder.WithGmoTracing(opts)));
        }

        [Fact]
        public void WithGmoTracing_NullEndpoint_Disabled_DoesNotThrow()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var opts = BuildOptions(false);
            opts.Otlp.Endpoint = null;
            var builder = services.AddOpenTelemetry();

            Assert.Null(Record.Exception(() => builder.WithGmoTracing(opts)));
        }

        [Fact]
        public void WithMetrics_WithEndpointAndConsole_ConfiguresWithoutError()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var opts = BuildOptions(true, console: true, metricsEndpoint: new Uri("http://localhost:4317"), meters: TestMeters);
            var builder = services.AddOpenTelemetry();
            var exception = Record.Exception(() => builder.WithMetrics(opts, _ => { }));
            Assert.Null(exception);
        }

        [Fact]
        public void ResolveMetricsExportProtocol_DefaultsToOtlpProtocol()
        {
            var opts = BuildOptions(true, meters: TestMeters);
            opts.Otlp!.Protocol = OtlpExportProtocol.Grpc;
            Assert.Equal(OtlpExportProtocol.Grpc, OpenTelemetryExtensions.ResolveMetricsExportProtocol(opts));
        }

        // An unset protocol and an absent Otlp must agree, and both must match the SDK default of
        // Grpc - otherwise metrics alone silently retarget from :4317 to HTTP.
        [Fact]
        public void ResolveMetricsExportProtocol_WithNothingConfigured_DefaultsToGrpc()
        {
            var unsetProtocol = BuildOptions(true, meters: TestMeters);
            unsetProtocol.Otlp!.Protocol = null;

            var absentOtlp = BuildOptions(true, meters: TestMeters);
            absentOtlp.Otlp = null!;

            Assert.Equal(OtlpExportProtocol.Grpc, OpenTelemetryExtensions.ResolveMetricsExportProtocol(unsetProtocol));
            Assert.Equal(OtlpExportProtocol.Grpc, OpenTelemetryExtensions.ResolveMetricsExportProtocol(absentOtlp));
        }

        [Fact]
        public void ResolveMetricsExportProtocol_Port4318_ForcesHttpProtobuf()
        {
            var opts = BuildOptions(true, metricsEndpoint: new Uri("https://otlp.nr-data.net:4318"), meters: TestMeters);
            opts.Otlp!.Protocol = OtlpExportProtocol.Grpc;
            Assert.Equal(OtlpExportProtocol.HttpProtobuf, OpenTelemetryExtensions.ResolveMetricsExportProtocol(opts));
        }

        [Fact]
        public void ResolveMetricsExportProtocol_MetricsPath_ForcesHttpProtobuf()
        {
            var opts = BuildOptions(true, metricsEndpoint: new Uri("https://otlp.example/v1/metrics"), meters: TestMeters);
            opts.Otlp!.Protocol = OtlpExportProtocol.Grpc;
            Assert.Equal(OtlpExportProtocol.HttpProtobuf, OpenTelemetryExtensions.ResolveMetricsExportProtocol(opts));
        }

        [Fact]
        public void ConfigureMetricsOtlpExporter_HttpProtobuf_AppendsMetricsPathAndHeaders()
        {
            var opts = BuildOptions(true, meters: TestMeters);
            opts.Otlp!.Protocol = OtlpExportProtocol.HttpProtobuf;
            opts.HeadersOverride = "api-key=nr-secret";
            opts.EnableGzipCompression = false;

            var exporter = new OtlpExporterOptions();
            OpenTelemetryExtensions.ConfigureMetricsOtlpExporter(
                exporter, null, opts, new Uri("https://otlp.nr-data.net:4318"));

            Assert.Equal(OtlpExportProtocol.HttpProtobuf, exporter.Protocol);
            Assert.Equal("https://otlp.nr-data.net:4318/v1/metrics", exporter.Endpoint.AbsoluteUri);
            Assert.Equal("api-key=nr-secret", exporter.Headers);
        }

        [Fact]
        public void ConfigureMetricsOtlpExporter_Grpc_KeepsHostWithoutSignalPath()
        {
            var opts = BuildOptions(true, meters: TestMeters);
            opts.Otlp!.Protocol = OtlpExportProtocol.Grpc;
            opts.MetricsEndpoint = null;
            opts.Otlp.Endpoint = new Uri("https://otlp.nr-data.net:4317");
            opts.EnableGzipCompression = false;

            var exporter = new OtlpExporterOptions { Headers = "already-set=1" };
            OpenTelemetryExtensions.ConfigureMetricsOtlpExporter(
                exporter, null, opts, new Uri("https://otlp.nr-data.net:4317"));

            Assert.Equal(OtlpExportProtocol.Grpc, exporter.Protocol);
            Assert.Equal(new Uri("https://otlp.nr-data.net:4317"), exporter.Endpoint);
            Assert.Equal("api-key=test-key", exporter.Headers);
        }

        // Framework hosts build their own MeterProvider and cannot reach the DI-only entry points, so
        // this extension is the only thing keeping their wiring from drifting from the package.
        [Fact]
        public void AddGmoMetrics_BuildsAWorkingProvider()
        {
            var opts = BuildOptions(true, meters: TestMeters);

            using var provider = Sdk.CreateMeterProviderBuilder().AddGmoMetrics(opts).Build();

            Assert.NotNull(provider);
        }

        [Fact]
        public void AddGmoMetrics_Disabled_LeavesTheBuilderAlone()
        {
            var opts = BuildOptions(false, meters: TestMeters);
            var builder = Sdk.CreateMeterProviderBuilder();

            Assert.Same(builder, builder.AddGmoMetrics(opts));
        }

        [Fact]
        public void AddGmoMetrics_NoMeterSources_LeavesTheBuilderAlone()
        {
            var opts = BuildOptions(true, meters: Array.Empty<string>());
            var builder = Sdk.CreateMeterProviderBuilder();

            Assert.Same(builder, builder.AddGmoMetrics(opts));
        }

        [Fact]
        public void AddGmoMetrics_NoResolvableEndpoint_Throws()
        {
            var opts = new NullEndpointOptions
            {
                Enabled = true,
                HeadersOverride = "api-key=test-key",
                MeterSourceNames = TestMeters,
            };

            var ex = Assert.Throws<InvalidOperationException>(
                () => Sdk.CreateMeterProviderBuilder().AddGmoMetrics(opts));

            Assert.Contains("Telemetry:MetricsEndpoint", ex.Message);
        }

        [Fact]
        public void AddGmoMetrics_NullOptions_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => Sdk.CreateMeterProviderBuilder().AddGmoMetrics(null!));
        }

        [Fact]
        public void ConfigureMetricsOtlpExporter_MapsIntervalAndTimeoutOntoTheReader()
        {
            var opts = BuildOptions(true, meters: TestMeters);
            opts.Otlp!.Protocol = OtlpExportProtocol.Grpc;
            opts.EnableGzipCompression = true;
            opts.MetricsExportDelayMs = 22;
            opts.MetricsExportTimeoutMs = 33;

            var exporter = new OtlpExporterOptions();
            var reader = new MetricReaderOptions();
            OpenTelemetryExtensions.ConfigureMetricsOtlpExporter(
                exporter, reader, opts, new Uri("https://otlp.nr-data.net:4317"));

            Assert.Equal(22, reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds);
            Assert.Equal(33, reader.PeriodicExportingMetricReaderOptions.ExportTimeoutMilliseconds);
            Assert.Equal(33, exporter.TimeoutMilliseconds);
            Assert.Equal(OtlpExportCompression.GZip, exporter.Compression);
        }

        // The batch timeout does not bound the request; left at the SDK's 10 s, a request that takes longer
        // loses its whole batch.
        [Fact]
        public void ConfigureTracesExport_RequestTimeoutFollowsTracesExportTimeout()
        {
            var opts = BuildOptions(true);
            opts.TracesExportTimeoutMs = 45000;
            opts.TracesExportDelayMs = 2000;
            opts.TracesMaxQueueSize = 4096;
            opts.TracesMaxBatchSize = 256;

            var exporter = new OtlpExporterOptions();
            OpenTelemetryExtensions.ConfigureTracesExport(exporter, opts);

            Assert.Equal(45000, exporter.TimeoutMilliseconds);
            Assert.Equal(45000, exporter.BatchExportProcessorOptions.ExporterTimeoutMilliseconds);
            Assert.Equal(2000, exporter.BatchExportProcessorOptions.ScheduledDelayMilliseconds);
            Assert.Equal(4096, exporter.BatchExportProcessorOptions.MaxQueueSize);
            Assert.Equal(256, exporter.BatchExportProcessorOptions.MaxExportBatchSize);
            Assert.Equal(OtlpExportCompression.GZip, exporter.Compression);
        }

        // Metrics are collected on an interval, never queued or batched, so writing these onto the batch
        // processor left every Metrics* option inert - which is how the interval went unapplied.
        [Fact]
        public void ConfigureMetricsOtlpExporter_LeavesBatchProcessorOptionsAlone()
        {
            var opts = BuildOptions(true, meters: TestMeters);
            opts.Otlp!.Protocol = OtlpExportProtocol.Grpc;
            opts.EnableGzipCompression = false;
            opts.MetricsMaxQueueSize = 11;
            opts.MetricsMaxBatchSize = 44;

            var exporter = new OtlpExporterOptions();
            OpenTelemetryExtensions.ConfigureMetricsOtlpExporter(
                exporter, new MetricReaderOptions(), opts, new Uri("https://otlp.nr-data.net:4317"));

            var untouched = new OtlpExporterOptions();
            Assert.Equal(untouched.BatchExportProcessorOptions.MaxQueueSize,
                exporter.BatchExportProcessorOptions.MaxQueueSize);
            Assert.Equal(untouched.BatchExportProcessorOptions.MaxExportBatchSize,
                exporter.BatchExportProcessorOptions.MaxExportBatchSize);
        }

        [Fact]
        public void ConfigureMetricsOtlpExporter_HttpProtobufGzip_UsesExporterCompression()
        {
            var opts = BuildOptions(true, meters: TestMeters);
            opts.Otlp!.Protocol = OtlpExportProtocol.HttpProtobuf;
            opts.EnableGzipCompression = true;
            opts.HeadersOverride = "api-key=gzip-key";

            var exporter = new OtlpExporterOptions();
            var defaultFactory = exporter.HttpClientFactory;
            OpenTelemetryExtensions.ConfigureMetricsOtlpExporter(
                exporter, null, opts, new Uri("https://otlp.nr-data.net:4318"));

            Assert.Equal(OtlpExportProtocol.HttpProtobuf, exporter.Protocol);
            Assert.Equal("https://otlp.nr-data.net:4318/v1/metrics", exporter.Endpoint.AbsoluteUri);
            Assert.Equal("api-key=gzip-key", exporter.Headers);
            Assert.Equal(OtlpExportCompression.GZip, exporter.Compression);
            Assert.Same(defaultFactory, exporter.HttpClientFactory);
        }

        [Fact]
        public void WithMetrics_HttpProtobufEndpoint_ConfiguresWithoutError()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var opts = BuildOptions(true, metricsEndpoint: new Uri("https://otlp.nr-data.net:4318"), meters: TestMeters);
            opts.Otlp!.Protocol = OtlpExportProtocol.HttpProtobuf;
            opts.EnableGzipCompression = true;
            var builder = services.AddOpenTelemetry();
            var exception = Record.Exception(() => builder.WithMetrics(opts, _ => { }));
            Assert.Null(exception);
        }
    }
}

#endif
