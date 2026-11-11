#if NETFRAMEWORK
using System;
using OpenTelemetry;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    // net48-only entry point: it never goes through AddGmoOpenTelemetry, so it needs its own
    // guard. It also did not check Enabled at all before, which is why the throw is gated on it.
    public class AddGmoInstrumentationTests
    {
        [Fact]
        public void NullEndpoint_Throws()
        {
            var options = new EndpointStubOptions { Enabled = true };

            var ex = Assert.Throws<InvalidOperationException>(() =>
                Sdk.CreateTracerProviderBuilder().AddGmoInstrumentation(options));

            Assert.Contains("Telemetry:Otlp:Endpoint", ex.Message);
            Assert.Contains("Telemetry:TraceSourceNames", ex.Message);
        }

        [Fact]
        public void NullEndpoint_Disabled_DoesNotThrow()
        {
            var options = new EndpointStubOptions { Enabled = false };

            Assert.Null(Record.Exception(() =>
                Sdk.CreateTracerProviderBuilder().AddGmoInstrumentation(options)));
        }

        // With Enabled false and a valid endpoint the method used to register a live OTLP exporter
        // anyway, so a host that believed telemetry was off kept exporting. It now short-circuits.
        [Fact]
        public void Disabled_WithEndpoint_ReturnsBuilderUnchanged()
        {
            var options = new EndpointStubOptions
            {
                Enabled = false,
                OtlpEndpoint = new Uri("https://otlp.example:4318"),
                EnableConsole = true,
            };

            var builder = Sdk.CreateTracerProviderBuilder();

            Assert.Same(builder, builder.AddGmoInstrumentation(options));
        }

        [Fact]
        public void PerSignalEnvironmentEndpoint_IsAccepted()
        {
            var options = new EndpointStubOptions { Enabled = true };
            var previous = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT");
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "https://otlp.example:4318/v1/traces");
            try
            {
                Assert.Null(Record.Exception(() =>
                    Sdk.CreateTracerProviderBuilder().AddGmoInstrumentation(options)));
            }
            finally
            {
                Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", previous);
            }
        }

        [Fact]
        public void NullEndpoint_NoTraceSources_DoesNotThrow()
        {
            var options = new EndpointStubOptions
            {
                Enabled = true,
                TraceSourceNames = [],
            };

            Assert.Null(Record.Exception(() =>
                Sdk.CreateTracerProviderBuilder().AddGmoInstrumentation(options)));
        }

        [Fact]
        public void WithEndpoint_DoesNotThrow()
        {
            var options = new EndpointStubOptions
            {
                Enabled = true,
                OtlpEndpoint = new Uri("https://otlp.example:4318"),
            };

            Assert.Null(Record.Exception(() =>
                Sdk.CreateTracerProviderBuilder().AddGmoInstrumentation(options)));
        }
    }
}
#endif
