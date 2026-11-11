using System.Diagnostics;
using GMO.OpenTelemetry.Serilog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using OpenTelemetry.Exporter;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

#if !NETFRAMEWORK

namespace GMO.OpenTelemetry.Tests
{
    [Collection("OpenTelemetryEnricherState")]
    public class SerilogOpenTelemetryExtensionsTests(FakeOtlpReceiver receiver)
    {
        private static IConfiguration EmptyConfig() =>
            new ConfigurationBuilder().Build();

        /// <summary>Captures enriched events so tests can assert on the properties the pipeline added.</summary>
        private sealed class CaptureSink : ILogEventSink
        {
            public List<LogEvent> Events { get; } = [];

            public void Emit(LogEvent logEvent) => Events.Add(logEvent);
        }

        [Fact]
        public void AddOpenTelemetry_Disabled_DoesNotConfigureSink()
        {
            var services = new ServiceCollection();
            using var provider = services.BuildServiceProvider();
            var options = new TestOptions { Enabled = false, EnableLogging = true };
            var logConfig = new LoggerConfiguration();

            // Should complete without touching the service provider
            var exception = Record.Exception(() =>
                logConfig.AddOpenTelemetry(options, EmptyConfig(), provider));
            Assert.Null(exception);
        }

        [Fact]
        public void AddOpenTelemetry_LoggingDisabled_DoesNotConfigureSink()
        {
            var services = new ServiceCollection();
            using var provider = services.BuildServiceProvider();
            var options = new TestOptions { Enabled = true, EnableLogging = false };
            var logConfig = new LoggerConfiguration();

            var exception = Record.Exception(() =>
                logConfig.AddOpenTelemetry(options, EmptyConfig(), provider));
            Assert.Null(exception);
        }

        [Fact]
        public void AddOpenTelemetry_NullServiceProvider_Throws()
        {
            var logConfig = new LoggerConfiguration();

            Assert.Throws<ArgumentNullException>(() =>
                logConfig.AddOpenTelemetry(EnabledOptions(), EmptyConfig(), (IServiceProvider)null!));
        }

        [Fact]
        public void AddOpenTelemetry_EnabledAndLogging_ConfiguresEnricherAndSink()
        {
            var attributeEnricher = new Mock<IAttributeEnricher>();
            attributeEnricher.Setup(e => e.Enrich(null))
                .Returns([]);

            var services = new ServiceCollection();
            services.AddSingleton(attributeEnricher.Object);
            using var provider = services.BuildServiceProvider();

            var options = EnabledOptions();
            var logConfig = new LoggerConfiguration();

            logConfig.AddOpenTelemetry(options, EmptyConfig(), provider);

            // Dispose the logger so the ChunkingOpenTelemetrySink's internal batching pipeline is torn down.
            using (var logger = logConfig.CreateLogger())
            {
                logger.Information("hello");
            }

            attributeEnricher.Verify(e => e.Enrich(null), Times.AtLeastOnce);
        }

        [Fact]
        public void AddOpenTelemetryWithEnricher_UsesSuppliedEnricher()
        {
            var attributeEnricher = new Mock<IAttributeEnricher>();
            attributeEnricher.Setup(e => e.Enrich(null))
                .Returns(new Dictionary<string, object> { ["trace.id"] = "supplied-enricher" });

            var logConfig = new LoggerConfiguration();
            var capture = new CaptureSink();
            logConfig.WriteTo.Sink(capture);

            logConfig.AddOpenTelemetryWithEnricher(EnabledOptions(), EmptyConfig(), attributeEnricher.Object);

            using (var logger = logConfig.CreateLogger())
            {
                logger.Information("hello");
            }

            attributeEnricher.Verify(e => e.Enrich(null), Times.AtLeastOnce);
            var captured = Assert.Single(capture.Events);
            Assert.Equal("supplied-enricher",
                ((ScalarValue)captured.Properties["trace.id"]).Value);
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void AddOpenTelemetryWithEnricher_Disabled_DoesNotConfigureSink(bool enabled, bool enableLogging)
        {
            var attributeEnricher = new Mock<IAttributeEnricher>();
            var logConfig = new LoggerConfiguration();
            var capture = new CaptureSink();
            logConfig.WriteTo.Sink(capture);

            logConfig.AddOpenTelemetryWithEnricher(
                new TestOptions { Enabled = enabled, EnableLogging = enableLogging }, EmptyConfig(), attributeEnricher.Object);

            using (var logger = logConfig.CreateLogger())
            {
                logger.Information("hello");
            }

            attributeEnricher.Verify(e => e.Enrich(It.IsAny<Activity>()), Times.Never);
            Assert.Single(capture.Events);
        }

        [Fact]
        public void AddOpenTelemetryWithEnricher_NullEnricher_Throws()
        {
            var logConfig = new LoggerConfiguration();

            Assert.Throws<ArgumentNullException>(() =>
                logConfig.AddOpenTelemetryWithEnricher(EnabledOptions(), EmptyConfig(), null!));
        }

        [Fact]
        public void AddOpenTelemetry_WithoutServiceProvider_FallbackEnricherEmitsCorrelationId()
        {
            using var listener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            };
            ActivitySource.AddActivityListener(listener);
            using var source = new ActivitySource("Test.SerilogFallbackEnricher");

            var logConfig = new LoggerConfiguration();
            var capture = new CaptureSink();
            logConfig.WriteTo.Sink(capture);

            logConfig.AddOpenTelemetry(EnabledOptions(), EmptyConfig());

            // AttributeEnricher only emits correlation data when an Activity is in scope.
            using (var activity = source.StartActivity("op"))
            using (var logger = logConfig.CreateLogger())
            {
                Assert.NotNull(activity);
                logger.Information("hello");
            }

            // The fallback must be wired to a CorrelationIdService; without one this property is absent.
            var captured = Assert.Single(capture.Events);
            Assert.True(captured.Properties.ContainsKey("CorrelationId"));
            Assert.False(string.IsNullOrEmpty(
                ((ScalarValue)captured.Properties["CorrelationId"]).Value as string));
        }

        [Fact]
        public void AddOpenTelemetry_WithoutServiceProvider_Disabled_DoesNotConfigureSink()
        {
            var options = new TestOptions { Enabled = false, EnableLogging = true };
            var logConfig = new LoggerConfiguration();
            var capture = new CaptureSink();
            logConfig.WriteTo.Sink(capture);

            logConfig.AddOpenTelemetry(options, EmptyConfig());

            using (var logger = logConfig.CreateLogger())
            {
                logger.Information("hello");
            }

            // Disabled means the enricher was never attached.
            var captured = Assert.Single(capture.Events);
            Assert.False(captured.Properties.ContainsKey("CorrelationId"));
            Assert.False(captured.Properties.ContainsKey(OpenTelemetryEnricher.LevelProp));
        }

        private TestOptions EnabledOptions() => new()
        {
            Enabled = true,
            EnableLogging = true,
            LoggingEndpoint = receiver.LogsEndpoint,
            HeadersOverride = "api-key=test-key",
            LogsMaxBatchSize = 10,
            LogsMaxQueueSize = 100,
            Otlp = new OtlpSettings { Protocol = OtlpExportProtocol.HttpProtobuf, Headers = "api-key=test-key" },
            MaxLogMessageLength = 130000,
        };
    }
}

#endif
