using System;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class AppendOtlpSignalPathTests
    {
        [Fact]
        public void AppendsWhenMissing()
        {
            var result = OpenTelemetryExtensions.AppendOtlpSignalPath(
                new Uri("https://otlp.nr-data.net:4318"), "v1/traces");
            Assert.Equal("https://otlp.nr-data.net:4318/v1/traces", result.AbsoluteUri);
        }

        [Fact]
        public void AppendsWhenTrailingSlashOnBase()
        {
            var result = OpenTelemetryExtensions.AppendOtlpSignalPath(
                new Uri("https://otlp.nr-data.net:4318/"), "v1/metrics");
            Assert.Equal("https://otlp.nr-data.net:4318/v1/metrics", result.AbsoluteUri);
        }

        [Fact]
        public void DoesNotDoubleAppendSameSignal()
        {
            var endpoint = new Uri("https://otlp.nr-data.net:4318/v1/metrics");
            var result = OpenTelemetryExtensions.AppendOtlpSignalPath(endpoint, "v1/metrics");
            Assert.Equal(endpoint.AbsoluteUri, result.AbsoluteUri);
        }

        [Fact]
        public void CaseInsensitiveSameSignalIsIdempotent()
        {
            var endpoint = new Uri("https://otlp.nr-data.net:4318/V1/Traces");
            var result = OpenTelemetryExtensions.AppendOtlpSignalPath(endpoint, "v1/traces");
            Assert.Equal(endpoint.AbsoluteUri, result.AbsoluteUri);
        }

        [Fact]
        public void ReplacesTracesPathWithMetrics()
        {
            var result = OpenTelemetryExtensions.AppendOtlpSignalPath(
                new Uri("https://otlp.nr-data.net:4318/v1/traces"), "v1/metrics");
            Assert.Equal("https://otlp.nr-data.net:4318/v1/metrics", result.AbsoluteUri);
        }

        [Fact]
        public void ReplacesMetricsPathWithTraces()
        {
            var result = OpenTelemetryExtensions.AppendOtlpSignalPath(
                new Uri("https://otlp.nr-data.net:4318/v1/metrics"), "v1/traces");
            Assert.Equal("https://otlp.nr-data.net:4318/v1/traces", result.AbsoluteUri);
        }

        [Fact]
        public void ReplacesLogsPathWithTraces()
        {
            var result = OpenTelemetryExtensions.AppendOtlpSignalPath(
                new Uri("https://otlp.nr-data.net:4318/v1/logs"), "v1/traces");
            Assert.Equal("https://otlp.nr-data.net:4318/v1/traces", result.AbsoluteUri);
        }

        [Fact]
        public void PreservesQueryStringWhenAppending()
        {
            var result = OpenTelemetryExtensions.AppendOtlpSignalPath(
                new Uri("https://otlp.nr-data.net:4318/?api-key=x"), "v1/metrics");
            Assert.Equal("https://otlp.nr-data.net:4318/v1/metrics?api-key=x", result.AbsoluteUri);
        }

        [Fact]
        public void PreservesQueryStringWhenReplacingSignal()
        {
            var result = OpenTelemetryExtensions.AppendOtlpSignalPath(
                new Uri("https://otlp.nr-data.net:4318/v1/traces?tenant=1"), "v1/metrics");
            Assert.Equal("https://otlp.nr-data.net:4318/v1/metrics?tenant=1", result.AbsoluteUri);
        }

        [Fact]
        public void NullEndpoint_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                OpenTelemetryExtensions.AppendOtlpSignalPath(null!, "v1/traces"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void NullOrWhitespaceSignalPath_Throws(string? signal)
        {
            Assert.Throws<ArgumentNullException>(() =>
                OpenTelemetryExtensions.AppendOtlpSignalPath(new Uri("https://otlp.nr-data.net:4318"), signal!));
        }
    }
}
