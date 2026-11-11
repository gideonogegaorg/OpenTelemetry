using System;
using GMO.OpenTelemetry.Serilog;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    /// <summary>
    /// Overrides <c>GetOtlpEndpoint</c> on every TFM: it is abstract on net48 and virtual on modern,
    /// so this stub compiles for both and lets the fallback be exercised on net48 too.
    /// </summary>
    internal class EndpointStubOptions : OpenTelemetryOptionsBase
    {
        public override string ServiceName => "Stub";
        public override string Version => "1.0.0";

        public Uri? OtlpEndpoint { get; set; }

        public override Uri? GetOtlpEndpoint() => OtlpEndpoint;

        protected override string? GetHeaders() => null;
    }

    // ResolveLoggingEndpoint reads through GetOtlpEndpoint rather than Otlp directly, so net48 gets
    // the same fallback as modern TFMs and the per-TFM branch is gone.
    public class ResolveLoggingEndpointTests
    {
        [Fact]
        public void PrefersLoggingEndpointOverOtlpEndpoint()
        {
            var options = new EndpointStubOptions
            {
                LoggingEndpoint = "https://logs.example/v1/logs",
                OtlpEndpoint = new Uri("https://traces.example:4317"),
            };

            Assert.Equal(
                "https://logs.example/v1/logs",
                ChunkingOpenTelemetrySink.ResolveLoggingEndpoint(options));
        }

        [Fact]
        public void FallsBackToOtlpEndpoint()
        {
            var options = new EndpointStubOptions { OtlpEndpoint = new Uri("https://otlp.example:4318") };

            Assert.Equal(
                "https://otlp.example:4318/",
                ChunkingOpenTelemetrySink.ResolveLoggingEndpoint(options));
        }

        // A blank LoggingEndpoint is not null, so unnormalised it would shadow the fallback and make
        // the fail-loud guard fire on a service that is configured correctly.
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void BlankLoggingEndpointDoesNotShadowFallback(string configured)
        {
            var options = new EndpointStubOptions
            {
                LoggingEndpoint = configured,
                OtlpEndpoint = new Uri("https://otlp.example:4318"),
            };

            Assert.Equal(
                "https://otlp.example:4318/",
                ChunkingOpenTelemetrySink.ResolveLoggingEndpoint(options));
        }

        [Fact]
        public void ReturnsNullWhenNothingIsConfigured()
        {
            Assert.Null(ChunkingOpenTelemetrySink.ResolveLoggingEndpoint(new EndpointStubOptions()));
        }

        [Fact]
        public void ThrowsOnNullOptions()
        {
            Assert.Throws<ArgumentNullException>(() =>
                ChunkingOpenTelemetrySink.ResolveLoggingEndpoint(null!));
        }
    }
}
