#if !NETFRAMEWORK
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using OpenTelemetry.Exporter;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class OtlpSettingsTests
    {
        private static TestOptions Bind(params (string Key, string? Value)[] values)
        {
            var data = new Dictionary<string, string?>();
            foreach (var (key, value) in values)
                data[key] = value;

            var config = new ConfigurationBuilder().AddInMemoryCollection(data).Build();
            return config.GetSection("Telemetry").Get<TestOptions>() ?? new TestOptions();
        }

        private static Func<string, string?> Environment(params (string Name, string? Value)[] values)
        {
            var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in values)
                map[name] = value;

            return name => map.TryGetValue(name, out var value) ? value : null;
        }

        // Binding used to write straight into OtlpExporterOptions, whose Endpoint setter rejects the
        // null an empty value binds to. It crashed even with Enabled false: binding is eager and total.
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("localhost:4317")]      // absolute, but Scheme=localhost - fails only at export
        [InlineData("/v1/traces")]          // relative - throws from UriBuilder/AbsoluteUri later
        [InlineData("not a uri at all")]
        public void Bind_UnusableEndpoint_LeavesEndpointUnset(string value)
        {
            var options = Bind(("Telemetry:Otlp:Endpoint", value));

            Assert.Null(options.Otlp.Endpoint);
        }

        [Fact]
        public void Bind_EmptyEndpoint_WithTelemetryDisabled_DoesNotThrow()
        {
            var options = Bind(("Telemetry:Enabled", "false"), ("Telemetry:Otlp:Endpoint", ""));

            Assert.False(options.Enabled);
            Assert.Null(options.Otlp.Endpoint);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Bind_BlankHeaders_LeavesHeadersUnset(string value)
        {
            var options = Bind(("Telemetry:Otlp:Headers", value));

            Assert.Null(options.Otlp.Headers);
        }

        // A blank Protocol used to throw from the binder itself - the same class of startup crash
        // this type exists to stop, just on a different key.
        [Fact]
        public void Bind_EmptyProtocol_DoesNotThrowAndLeavesProtocolUnset()
        {
            var options = Bind(("Telemetry:Otlp:Protocol", ""));

            Assert.Null(options.Otlp.Protocol);
        }

        // Known gap, pinned deliberately: the binder's nullable short-circuit tests IsNullOrEmpty,
        // not IsNullOrWhiteSpace, and it converts before any setter here runs. Closing it would mean
        // binding Protocol as a string. If a future binder handles this, delete the test and the note.
        [Fact]
        public void Bind_WhitespaceProtocol_StillThrowsFromTheBinder()
        {
            Assert.Throws<InvalidOperationException>(() => Bind(("Telemetry:Otlp:Protocol", "   ")));
        }

        [Fact]
        public void Bind_PopulatedSection_RoundTripsEveryValue()
        {
            var options = Bind(
                ("Telemetry:Otlp:Endpoint", "http://localhost:4318"),
                ("Telemetry:Otlp:Headers", "api-key=secret"),
                ("Telemetry:Otlp:Protocol", "HttpProtobuf"));

            Assert.Equal(new Uri("http://localhost:4318"), options.Otlp.Endpoint);
            Assert.Equal("api-key=secret", options.Otlp.Headers);
            Assert.Equal(OtlpExportProtocol.HttpProtobuf, options.Otlp.Protocol);
        }

        // Protocol must stay null when unset: a default here silently overrides
        // OTEL_EXPORTER_OTLP_PROTOCOL, and flips metrics export via ResolveMetricsExportProtocol.
        [Fact]
        public void Bind_AbsentSection_LeavesEverythingUnset()
        {
            var options = Bind(("Telemetry:Enabled", "true"));

            Assert.Null(options.Otlp.Endpoint);
            Assert.Null(options.Otlp.Headers);
            Assert.Null(options.Otlp.Protocol);
        }

        [Fact]
        public void ApplyTo_WithNullTarget_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new OtlpSettings().ApplyTo(null!));
        }

        [Fact]
        public void ApplyTo_WithNothingConfigured_LeavesExporterDefaults()
        {
            var exporter = new OtlpExporterOptions { Headers = "already-set=1" };
            var before = exporter.Endpoint;

            new OtlpSettings().ApplyTo(exporter);

            Assert.Equal(before, exporter.Endpoint);
            Assert.Equal("already-set=1", exporter.Headers);
        }

        [Fact]
        public void ApplyTo_WithValuesConfigured_OverwritesExporter()
        {
            var exporter = new OtlpExporterOptions { Headers = "already-set=1" };

            new OtlpSettings
            {
                Endpoint = new Uri("http://localhost:4318"),
                Headers = "api-key=k",
                Protocol = OtlpExportProtocol.HttpProtobuf
            }.ApplyTo(exporter);

            Assert.Equal(new Uri("http://localhost:4318"), exporter.Endpoint);
            Assert.Equal("api-key=k", exporter.Headers);
            Assert.Equal(OtlpExportProtocol.HttpProtobuf, exporter.Protocol);
        }

        // LoggingEndpoint and MetricsEndpoint override the OTLP endpoint per signal. A blank value is
        // not null, so unnormalised it shadows the fallback and silently disables that signal -
        // and a whitespace MetricsEndpoint binds to a relative Uri that crashes Uri.Port later.
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Bind_BlankLoggingEndpoint_FallsThroughToOtlpEndpoint(string value)
        {
            var options = Bind(
                ("Telemetry:LoggingEndpoint", value),
                ("Telemetry:Otlp:Endpoint", "http://collector:4318"));

            Assert.Null(options.LoggingEndpoint);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("localhost:4318")]
        [InlineData("/v1/metrics")]
        public void Bind_UnusableMetricsEndpoint_LeavesItUnset(string value)
        {
            var options = Bind(("Telemetry:MetricsEndpoint", value));

            Assert.Null(options.MetricsEndpoint);
        }

        // Blank is the documented silent opt-out; a value that was present but unusable is a typo and
        // must be reported, because it otherwise falls back to the trace endpoint unnoticed.
        [Theory]
        [InlineData("Telemetry:MetricsEndpoint", "localhost:4318")]
        [InlineData("Telemetry:LoggingEndpoint", "otlp.example.com")]
        [InlineData("Telemetry:Otlp:Endpoint", "localhost:4317")]
        public void ThrowIfEndpointsMalformed_ReportsKeyAndValue(string key, string value)
        {
            var options = Bind((key, value));

            var ex = Assert.Throws<InvalidOperationException>(() => options.ThrowIfEndpointsMalformed());

            Assert.Contains(key, ex.Message);
            Assert.Contains(value, ex.Message);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void ThrowIfEndpointsMalformed_IgnoresBlankValues(string value)
        {
            var options = Bind(
                ("Telemetry:MetricsEndpoint", value),
                ("Telemetry:LoggingEndpoint", value),
                ("Telemetry:Otlp:Endpoint", value));

            Assert.Null(Record.Exception(() => options.ThrowIfEndpointsMalformed()));
        }

        [Fact]
        public void ThrowIfEndpointsMalformed_SilentWhenEverythingIsUsable()
        {
            var options = Bind(
                ("Telemetry:Otlp:Endpoint", "https://otlp.example:4317"),
                ("Telemetry:MetricsEndpoint", "https://otlp.example:4318/v1/metrics"),
                ("Telemetry:LoggingEndpoint", "https://otlp.example"));

            Assert.Null(Record.Exception(() => options.ThrowIfEndpointsMalformed()));
        }

        [Fact]
        public void Bind_ValidMetricsEndpoint_IsKept()
        {
            var options = Bind(("Telemetry:MetricsEndpoint", "https://otlp.nr-data.net:4318/v1/metrics"));

            Assert.Equal(new Uri("https://otlp.nr-data.net:4318/v1/metrics"), options.MetricsEndpoint);
        }

        [Fact]
        public void GetOtlpEndpoint_ReflectsBoundValue()
        {
            var options = Bind(("Telemetry:Otlp:Endpoint", "http://localhost:4317"));

            Assert.Equal(new Uri("http://localhost:4317"), options.GetOtlpEndpoint());
        }
    }
}
#endif
