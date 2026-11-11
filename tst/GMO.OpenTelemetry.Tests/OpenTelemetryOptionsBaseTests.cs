using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    // Concrete implementation for testing the abstract base
    internal class TestOptions : OpenTelemetryOptionsBase
    {
        public override string ServiceName => "TestService";
        public override string Version => "1.0.0";

        public string? HeadersOverride { get; set; }

        protected override string? GetHeaders() => HeadersOverride;

#if NETFRAMEWORK
        public override Uri? GetOtlpEndpoint() => null;
#endif
    }

#if !NETFRAMEWORK
    internal class ConfiguredHeadersOptions : OpenTelemetryOptionsBase
    {
        public override string ServiceName => "TestService";
        public override string Version => "1.0.0";
    }
#endif

    public class OpenTelemetryOptionsBaseTests
    {
        private static TestOptions BuildOptions(string? headers = null,
            OpenTelemetryServiceProvider provider = OpenTelemetryServiceProvider.NewRelic)
        {
            return new TestOptions
            {
                HeadersOverride = headers,
                ServiceProvider = provider,
            };
        }

        [Fact]
        public void GetOtlpHeaders_ReturnsRawConfiguredHeaders()
        {
            var opts = BuildOptions("Authorization=Api-Token my-dt-token",
                OpenTelemetryServiceProvider.Dynatrace);
            Assert.Equal("Authorization=Api-Token my-dt-token", opts.GetOtlpHeaders());
        }

        [Fact]
        public void GetApiKey_NullHeaders_ReturnsNull()
        {
            var opts = BuildOptions(null);
            Assert.Null(opts.GetApiKey());
        }

        [Fact]
        public void GetApiKey_EmptyHeaders_ReturnsNull()
        {
            var opts = BuildOptions("");
            Assert.Null(opts.GetApiKey());
        }

        // 4,094 in NRDB plus a 128,000-byte blob is what New Relic keeps of one value; the default is
        // sized to that window, so a larger one silently loses the tail.
        [Fact]
        public void MaxLogMessageLength_DefaultsToWhatTheVendorRetains()
        {
            Assert.Equal(16000, new TestOptions().MaxLogMessageLength);
        }

        // Value 0 is what configuration binding leaves when ServiceProvider is omitted.
        [Fact]
        public void ServiceProvider_NotSet_DefaultsToCollector()
        {
            Assert.Equal(OpenTelemetryServiceProvider.Collector, new TestOptions().ServiceProvider);
            Assert.Equal(OpenTelemetryServiceProvider.Collector, default(OpenTelemetryServiceProvider));
        }

        [Fact]
        public void GetApiKey_Collector_AuthorizationHeader_IsNotTreatedAsApiKey()
        {
            var opts = BuildOptions("Authorization=Api-Token my-token", OpenTelemetryServiceProvider.Collector);
            Assert.Null(opts.GetApiKey());
        }

        [Fact]
        public void GetApiKey_NewRelic_ApiKeyHeader_ReturnsKey()
        {
            var opts = BuildOptions("api-key=my-secret-key");
            Assert.Equal("my-secret-key", opts.GetApiKey());
        }

        [Fact]
        public void GetApiKey_NewRelic_ApiKeyWithOtherHeaders_ReturnsKey()
        {
            var opts = BuildOptions("other=val;api-key=my-key");
            Assert.Equal("my-key", opts.GetApiKey());
        }

        [Fact]
        public void GetApiKey_Dynatrace_AuthorizationApiToken_ReturnsToken()
        {
            var opts = BuildOptions("Authorization=Api-Token my-dt-token",
                OpenTelemetryServiceProvider.Dynatrace);
            Assert.Equal("my-dt-token", opts.GetApiKey());
        }

        [Fact]
        public void GetApiKey_Dynatrace_AuthorizationWithoutPrefix_ReturnsValue()
        {
            var opts = BuildOptions("Authorization=bare-value",
                OpenTelemetryServiceProvider.Dynatrace);
            Assert.Equal("bare-value", opts.GetApiKey());
        }

        [Fact]
        public void GetApiKey_Dynatrace_EmptyAuthorizationValue_ReturnsNull()
        {
            var opts = BuildOptions("Authorization=",
                OpenTelemetryServiceProvider.Dynatrace);
            Assert.Null(opts.GetApiKey());
        }

        [Fact]
        public void GetApiKey_Dynatrace_ApiTokenPrefixWithPaddedToken_ReturnsTrimmedToken()
        {
            var opts = BuildOptions("Authorization=Api-Token    my-dt-token",
                OpenTelemetryServiceProvider.Dynatrace);
            Assert.Equal("my-dt-token", opts.GetApiKey());
        }

        // Only a trailing space would leave the token blank, and the header value is trimmed first.
        [Fact]
        public void GetApiKey_Dynatrace_ApiTokenPrefixWithoutToken_ReturnsTheValueUnstripped()
        {
            var opts = BuildOptions("Authorization=Api-Token   ",
                OpenTelemetryServiceProvider.Dynatrace);
            Assert.Equal("Api-Token", opts.GetApiKey());
        }

        [Fact]
        public void GetApiKey_NoMatchingHeader_ReturnsNull()
        {
            var opts = BuildOptions("some-header=some-value");
            Assert.Null(opts.GetApiKey());
        }

        [Fact]
        public void Attributes_ContainsServiceName()
        {
            var opts = new TestOptions { EnvironmentName = "QA" };
            Assert.Equal("TestService", opts.Attributes["service.name"]);
        }

        [Fact]
        public void Attributes_ContainsVersion()
        {
            var opts = new TestOptions();
            Assert.Equal("1.0.0", opts.Attributes["service.version"]);
        }

        [Fact]
        public void Attributes_ContainsEnvironmentName()
        {
            var opts = new TestOptions { EnvironmentName = "QA" };
            Assert.Equal("QA", opts.Attributes["environment.name"]);
        }

        [Fact]
        public void Attributes_EmptyEnvironmentName_UsesMachineName()
        {
            var opts = new TestOptions { EnvironmentName = "" };
            Assert.Equal(System.Environment.MachineName, opts.Attributes["environment.name"]);
        }

        [Fact]
        public void Attributes_WhitespaceNames_UseMachineName()
        {
            var opts = new TestOptions { EnvironmentName = "  ", InstanceName = " " };

            Assert.Equal(System.Environment.MachineName, opts.Attributes["environment.name"]);
            Assert.Equal(System.Environment.MachineName, opts.Attributes["instance.name"]);
            Assert.Equal(System.Environment.MachineName, opts.Attributes["hostname"]);
        }

        [Fact]
        public void Attributes_InstanceName_NamesTheInstanceAndHost()
        {
            var opts = new TestOptions { InstanceName = "web-01" };

            Assert.Equal("web-01", opts.Attributes["instance.name"]);
            Assert.Equal("web-01", opts.Attributes["hostname"]);
        }

#if !NETFRAMEWORK
        [Fact]
        public void OtlpUnset_ResolvesNothingAndIsNotMalformed()
        {
            var opts = new ConfiguredHeadersOptions { Otlp = null! };

            Assert.Null(opts.GetOtlpEndpoint());
            Assert.Null(opts.GetOtlpHeaders());
            opts.ThrowIfEndpointsMalformed();
        }

        [Fact]
        public void GetOtlpHeaders_DefaultsToOtlpHeaders()
        {
            var opts = new ConfiguredHeadersOptions { Otlp = new OtlpSettings { Headers = "api-key=from-config" } };

            Assert.Equal("api-key=from-config", opts.GetOtlpHeaders());
        }

        [Fact]
        public void GetOtlpEndpoint_DefaultsToOtlpEndpoint()
        {
            var opts = new TestOptions();
            opts.Otlp = new OtlpSettings { Endpoint = new System.Uri("http://localhost:4317") };
            Assert.Equal(new System.Uri("http://localhost:4317"), opts.GetOtlpEndpoint());
        }
#endif

        [Fact]
        public void DefaultOptions_IncludeSourceLocation_IsFalse()
        {
            var opts = new TestOptions();
            Assert.True(opts.IncludeSourceLocation);
        }

        [Fact]
        public void DefaultOptions_TracesMaxBatchSize_Is512()
        {
            var opts = new TestOptions();
            Assert.Equal(512, opts.TracesMaxBatchSize);
        }

        [Fact]
        public void DefaultOptions_TracesMaxQueueSize_Is2048()
        {
            var opts = new TestOptions();
            Assert.Equal(2048, opts.TracesMaxQueueSize);
        }

        [Fact]
        public void DefaultOptions_LogsMaxBatchSize_Is64()
        {
            var opts = new TestOptions();
            Assert.Equal(64, opts.LogsMaxBatchSize);
        }

        [Fact]
        public void DefaultOptions_LogsMaxQueueSize_Is2048()
        {
            var opts = new TestOptions();
            Assert.Equal(2048, opts.LogsMaxQueueSize);
        }

        [Fact]
        public void DefaultOptions_OverflowChannelCapacity_Is1000()
        {
            var opts = new TestOptions();
            Assert.Equal(1000, opts.OverflowChannelCapacity);
        }

        [Fact]
        public void DefaultOptions_OverflowBatchSize_Is50()
        {
            var opts = new TestOptions();
            Assert.Equal(50, opts.OverflowBatchSize);
        }

        [Fact]
        public void DefaultOptions_EnableGzipCompression_IsTrue()
        {
            var opts = new TestOptions();
            Assert.True(opts.EnableGzipCompression);
        }

        [Fact]
        public void DefaultOptions_MaxAttributeValueLength_Is4000()
        {
            var opts = new TestOptions();
            Assert.Equal(4000, opts.MaxAttributeValueLength);
        }

        [Fact]
        public void DefaultOptions_ExportTimeoutMs_Is30000()
        {
            var opts = new TestOptions();
            Assert.Equal(30000, opts.TracesExportTimeoutMs);
            Assert.Equal(30000, opts.MetricsExportTimeoutMs);
            Assert.Equal(30000, opts.LogsExportTimeoutMs);
        }

        [Fact]
        public void Attributes_ReturnsCachedInstance()
        {
            var opts = new TestOptions { EnvironmentName = "Test" };
            var first = opts.Attributes;
            var second = opts.Attributes;
            Assert.Same(first, second);
        }
    }
}
