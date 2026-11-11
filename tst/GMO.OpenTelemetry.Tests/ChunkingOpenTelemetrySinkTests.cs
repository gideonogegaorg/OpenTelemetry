using GMO.OpenTelemetry.Serilog;
using Microsoft.Extensions.Configuration;
using Moq;
using OpenTelemetry.Exporter;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;
using SinkProtocol = Serilog.Sinks.OpenTelemetry.OtlpProtocol;

#if !NETFRAMEWORK

namespace GMO.OpenTelemetry.Tests
{
    public class ChunkingOpenTelemetrySinkTests(FakeOtlpReceiver receiver)
    {
        private static IConfiguration EmptyConfig() =>
            new ConfigurationBuilder().Build();

        private TestOptions BuildOptions(
            string? endpoint = null,
            OtlpExportProtocol protocol = OtlpExportProtocol.HttpProtobuf,
            string headers = "api-key=test-key",
            int maxLogLength = 1000)
        {
            return new TestOptions
            {
                Enabled = true,
                LoggingEndpoint = endpoint ?? receiver.LogsEndpoint,
                HeadersOverride = headers,
                LogsMaxBatchSize = 100,
                LogsMaxQueueSize = 1000,
                MaxLogMessageLength = maxLogLength,
                Otlp = new OtlpSettings { Protocol = protocol, Headers = headers },
            };
        }

        [Fact]
        public void ParseHeaders_RepeatedKey_KeepsTheOtherHeaders()
        {
            // A duplicate must not fail the parse: that would drop every header, the API key included.
            var headers = ChunkingOpenTelemetrySink.ParseHeaders("api-key=a, x-tenant=t, api-key=b");

            Assert.Equal("b", headers["api-key"]);
            Assert.Equal("t", headers["x-tenant"]);
        }

        [Fact]
        public void ParseHeaders_KeepsEqualsSignsInTheValue()
        {
            var headers = ChunkingOpenTelemetrySink.ParseHeaders("Authorization=Api-Token abc==");

            Assert.Equal("Api-Token abc==", headers["Authorization"]);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not-a-header")]
        [InlineData("=no-key")]
        public void ParseHeaders_NothingUsable_IsEmptyNotNull(string? headers)
        {
            Assert.Empty(ChunkingOpenTelemetrySink.ParseHeaders(headers));
        }

        [Fact]
        public void ApplyGzipCompression_HttpProtobuf_ReturnsTheCompressingHandler()
        {
            var headers = new Dictionary<string, string> { ["api-key"] = "k" };

            using var handler = ChunkingOpenTelemetrySink.ApplyGzipCompression(new TestOptions(), SinkProtocol.HttpProtobuf, headers);

            Assert.IsType<GzipCompressingHandler>(handler);
            Assert.False(headers.ContainsKey(ChunkingOpenTelemetrySink.GrpcRequestEncodingHeader));
        }

        // A handler cannot compress gRPC; the header makes the sink's Grpc.Net.Client compress each message.
        [Fact]
        public void ApplyGzipCompression_Grpc_SetsTheRequestEncodingHeaderInstead()
        {
            var headers = new Dictionary<string, string> { ["api-key"] = "k" };

            var handler = ChunkingOpenTelemetrySink.ApplyGzipCompression(new TestOptions(), SinkProtocol.Grpc, headers);

            Assert.Null(handler);
            Assert.Equal("gzip", headers[ChunkingOpenTelemetrySink.GrpcRequestEncodingHeader]);
            Assert.Equal("k", headers["api-key"]);
        }

        [Theory]
        [InlineData(SinkProtocol.HttpProtobuf)]
        [InlineData(SinkProtocol.Grpc)]
        public void ApplyGzipCompression_Disabled_CompressesNothing(SinkProtocol protocol)
        {
            var headers = new Dictionary<string, string>
            {
                ["api-key"] = "k",
                [ChunkingOpenTelemetrySink.GrpcRequestEncodingHeader] = "gzip",
            };

            var handler = ChunkingOpenTelemetrySink.ApplyGzipCompression(new TestOptions { EnableGzipCompression = false }, protocol, headers);

            Assert.Null(handler);
            Assert.False(headers.ContainsKey(ChunkingOpenTelemetrySink.GrpcRequestEncodingHeader));
            Assert.Equal("k", headers["api-key"]);
        }

        // A gRPC connection that dies without a reset is only detected by keepalive pings; without them
        // the sink retries every batch on the dead connection and log export stops for good.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void CreateHttpMessageHandler_Grpc_GetsKeepAliveWhetherOrNotGzipIsOn(bool gzip)
        {
            var headers = new Dictionary<string, string> { ["api-key"] = "k" };

            using var handler = ChunkingOpenTelemetrySink.CreateHttpMessageHandler(
                new TestOptions { EnableGzipCompression = gzip }, SinkProtocol.Grpc, headers);

            var sockets = Assert.IsType<System.Net.Http.SocketsHttpHandler>(handler);
            Assert.Equal(TimeSpan.FromSeconds(30), sockets.KeepAlivePingDelay);
            Assert.Equal(TimeSpan.FromSeconds(10), sockets.KeepAlivePingTimeout);
            Assert.Equal(System.Net.Http.HttpKeepAlivePingPolicy.Always, sockets.KeepAlivePingPolicy);
            Assert.Equal(TimeSpan.FromMinutes(5), sockets.PooledConnectionLifetime);
            Assert.True(sockets.EnableMultipleHttp2Connections);
            Assert.Equal(gzip, headers.ContainsKey(ChunkingOpenTelemetrySink.GrpcRequestEncodingHeader));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void CreateHttpMessageHandler_HttpProtobuf_CompressesOnlyWhenGzipIsOn(bool gzip)
        {
            var headers = new Dictionary<string, string>();

            using var handler = ChunkingOpenTelemetrySink.CreateHttpMessageHandler(
                new TestOptions { EnableGzipCompression = gzip }, SinkProtocol.HttpProtobuf, headers);

            if (gzip)
                Assert.IsType<GzipCompressingHandler>(handler);
            else
                Assert.Null(handler);
            Assert.Empty(headers);
        }

        [Fact]
        public void BuildChunkEvent_KeepsTheSourceTraceAndSpan()
        {
            var traceId = System.Diagnostics.ActivityTraceId.CreateRandom();
            var spanId = System.Diagnostics.ActivitySpanId.CreateRandom();
            var source = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null,
                new MessageTemplateParser().Parse("long message"), [], traceId, spanId);

            var chunk = ChunkingOpenTelemetrySink.BuildChunkEvent(source, "long", 0, 2);

            Assert.Equal(traceId, chunk.TraceId);
            Assert.Equal(spanId, chunk.SpanId);
        }

        [Fact]
        public void BuildChunkEvent_NoSourceTrace_LeavesItUnset()
        {
            var chunk = ChunkingOpenTelemetrySink.BuildChunkEvent(BuildLogEvent("long message"), "long", 1, 2);

            Assert.Null(chunk.TraceId);
            Assert.Null(chunk.SpanId);
        }

        [Fact]
        public void BuildChunkEvent_MarksTheChunkAndKeepsTheExceptionForTheLast()
        {
            var error = new InvalidOperationException("boom");
            var source = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Error, error,
                new MessageTemplateParser().Parse("long message"),
                [new LogEventProperty("JobName", new ScalarValue("nightly"))]);

            var first = ChunkingOpenTelemetrySink.BuildChunkEvent(source, "long {x}", 0, 2);
            var last = ChunkingOpenTelemetrySink.BuildChunkEvent(source, " message", 1, 2);

            Assert.Equal("long {x}", first.RenderMessage());
            Assert.Equal(1, ((ScalarValue)first.Properties["chunk.index"]).Value);
            Assert.Equal(2, ((ScalarValue)first.Properties["chunk.count"]).Value);
            Assert.True(Assert.IsType<bool>(((ScalarValue)first.Properties["overflow"]).Value));
            Assert.Equal("nightly", ((ScalarValue)first.Properties["JobName"]).Value);
            Assert.Null(first.Exception);
            Assert.Same(error, last.Exception);
        }

        [Fact]
        public void BuildChunkEvent_LaterChunksCarryOnlyShortScalarContext()
        {
            var source = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null,
                new MessageTemplateParser().Parse("long message"),
                [
                    new LogEventProperty("JobName", new ScalarValue("nightly")),
                    new LogEventProperty("Payload", new ScalarValue(new string('p', ChunkingOpenTelemetrySink.ChunkContextValueLength + 1))),
                    new LogEventProperty("Employee", new StructureValue([new LogEventProperty("Id", new ScalarValue(1))])),
                ]);

            var first = ChunkingOpenTelemetrySink.BuildChunkEvent(source, "long", 0, 2, "c1");
            var second = ChunkingOpenTelemetrySink.BuildChunkEvent(source, " message", 1, 2, "c1");

            Assert.True(first.Properties.ContainsKey("Payload"));
            Assert.True(first.Properties.ContainsKey("Employee"));
            Assert.Equal("nightly", ((ScalarValue)second.Properties["JobName"]).Value);
            Assert.False(second.Properties.ContainsKey("Payload"));
            Assert.False(second.Properties.ContainsKey("Employee"));
            Assert.Equal("c1", ((ScalarValue)first.Properties["chunk.id"]).Value);
            Assert.Equal("c1", ((ScalarValue)second.Properties["chunk.id"]).Value);
        }

        private static LogEvent BuildLogEvent(string messageText, LogEventLevel level = LogEventLevel.Information)
        {
            var template = new MessageTemplateParser().Parse(messageText);
            return new LogEvent(DateTimeOffset.UtcNow, level, null, template, []);
        }

        [Fact]
        public void Constructor_NullOptions_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new ChunkingOpenTelemetrySink(null!, EmptyConfig()));
        }

        [Fact]
        public void Constructor_NullConfiguration_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new ChunkingOpenTelemetrySink(BuildOptions(), null!));
        }

        // LoggingEndpoint="" is not null (bypasses ??) but IsNullOrWhiteSpace makes it unusable.
        private static TestOptions NoEndpointOptions() => new()
        {
            Enabled = true,
            LoggingEndpoint = "",
            HeadersOverride = "api-key=test",
            LogsMaxBatchSize = 100,
            LogsMaxQueueSize = 1000,
            MaxLogMessageLength = 1000,
            Otlp = new OtlpSettings { Headers = "api-key=test" },
        };

        // EnableLogging with nowhere to send can only be a misconfiguration, and silence means the
        // logs are missing until someone needs them. The message must name the keys it checked.
        [Fact]
        public void Constructor_NoEndpoint_Throws()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                new ChunkingOpenTelemetrySink(NoEndpointOptions(), EmptyConfig()));

            Assert.Contains("Telemetry:LoggingEndpoint", ex.Message);
            Assert.Contains("OTEL_EXPORTER_OTLP_ENDPOINT", ex.Message);
            Assert.Contains("Telemetry:EnableLogging", ex.Message);
        }

        // Cross-TFM coverage of ResolveLoggingEndpoint lives in ResolveLoggingEndpointTests, since this
        // file is modern-only (it touches Otlp) and the net48 fallback is the point being verified.
        [Fact]
        public void ResolveLoggingEndpoint_FallsBackToOtlpEndpoint()
        {
            var opts = NoEndpointOptions();
            opts.Otlp.Endpoint = new Uri("http://collector:4318");

            Assert.Equal(
                "http://collector:4318/",
                ChunkingOpenTelemetrySink.ResolveLoggingEndpoint(opts));
        }

        [Fact]
        public void Constructor_HttpProtobufProtocol_DoesNotThrow()
        {
            using var sink = new ChunkingOpenTelemetrySink(
                BuildOptions(protocol: OtlpExportProtocol.HttpProtobuf), EmptyConfig());
            Assert.NotNull(sink);
        }

        [Fact]
        public void Constructor_GrpcProtocol_DoesNotThrow()
        {
            using var sink = new ChunkingOpenTelemetrySink(
                BuildOptions(protocol: OtlpExportProtocol.Grpc), EmptyConfig());
            Assert.NotNull(sink);
        }

        [Fact]
        public void Constructor_WithValidHeaders_DoesNotThrow()
        {
            using var sink = new ChunkingOpenTelemetrySink(
                BuildOptions(headers: "api-key=abc123"), EmptyConfig());
            Assert.NotNull(sink);
        }

        [Fact]
        public void Constructor_WithMalformedHeaders_DoesNotThrow()
        {
            // Malformed headers should be caught and treated as null
            using var sink = new ChunkingOpenTelemetrySink(
                BuildOptions(headers: "not=valid=header=format"), EmptyConfig());
            Assert.NotNull(sink);
        }

        // A repeated key is the one header shape that actually throws: ToDictionary rejects the
        // duplicate, and the sink has to fall back to no headers rather than failing construction.
        [Fact]
        public void Constructor_DuplicateHeaderKey_FallsBackInsteadOfThrowing()
        {
            using var sink = new ChunkingOpenTelemetrySink(
                BuildOptions(headers: "api-key=first,api-key=second"), EmptyConfig());
            Assert.NotNull(sink);
        }

        // Neither :4318 nor /v1/logs, so the endpoint implies nothing and the configured protocol wins.
        [Fact]
        public void Constructor_EndpointWithoutHttpHints_UsesConfiguredProtocol()
        {
            using var sink = new ChunkingOpenTelemetrySink(
                BuildOptions(endpoint: "http://collector.internal:4317", protocol: OtlpExportProtocol.Grpc),
                EmptyConfig());
            Assert.NotNull(sink);
        }

        [Fact]
        public void Emit_NullLogEvent_DoesNotThrow()
        {
            using var sink = new ChunkingOpenTelemetrySink(BuildOptions(), EmptyConfig());
            var exception = Record.Exception(() => sink.Emit(null!));
            Assert.Null(exception);
        }

        [Fact]
        public void Emit_ShortMessage_PassesThroughWithoutChunking()
        {
            using var sink = new ChunkingOpenTelemetrySink(BuildOptions(maxLogLength: 1000), EmptyConfig());
            var logEvent = BuildLogEvent("short message");
            // Must not throw — inner logger write may fail silently
            var exception = Record.Exception(() => sink.Emit(logEvent));
            Assert.Null(exception);
        }

        [Fact]
        public void Emit_LongMessage_ChunksWithoutThrow()
        {
            using var sink = new ChunkingOpenTelemetrySink(BuildOptions(maxLogLength: 10), EmptyConfig());
            var logEvent = BuildLogEvent(new string('x', 35)); // 35 chars → 4 chunks of 10
            var exception = Record.Exception(() => sink.Emit(logEvent));
            Assert.Null(exception);
        }

        [Fact]
        public void Emit_AfterDispose_DoesNotThrow()
        {
            var sink = new ChunkingOpenTelemetrySink(BuildOptions(), EmptyConfig());
            sink.Dispose();
            var exception = Record.Exception(() => sink.Emit(BuildLogEvent("test")));
            Assert.Null(exception);
        }

        [Fact]
        public void Dispose_CanBeCalledMultipleTimes()
        {
            var sink = new ChunkingOpenTelemetrySink(BuildOptions(), EmptyConfig());
            sink.Dispose();
            var exception = Record.Exception(() => sink.Dispose());
            Assert.Null(exception);
        }

        [Fact]
        public void Emit_ZeroMaxLength_PassesThroughWithoutChunking()
        {
            // MaxLogMessageLength <= 0 means "don't chunk"
            using var sink = new ChunkingOpenTelemetrySink(BuildOptions(maxLogLength: 0), EmptyConfig());
            var logEvent = BuildLogEvent("any message");
            var exception = Record.Exception(() => sink.Emit(logEvent));
            Assert.Null(exception);
        }

        [Fact]
        public void Emit_ExactlyMaxLength_DoesNotChunk()
        {
            const int max = 10;
            using var sink = new ChunkingOpenTelemetrySink(BuildOptions(maxLogLength: max), EmptyConfig());
            var logEvent = BuildLogEvent(new string('a', max)); // exactly at limit
            var exception = Record.Exception(() => sink.Emit(logEvent));
            Assert.Null(exception);
        }

        [Fact]
        public void Emit_WithException_IncludesExceptionOnLastChunk()
        {
            using var sink = new ChunkingOpenTelemetrySink(BuildOptions(maxLogLength: 5), EmptyConfig());
            var template = new MessageTemplateParser().Parse(new string('x', 12));
            var logEvent = new LogEvent(
                DateTimeOffset.UtcNow,
                LogEventLevel.Error,
                new InvalidOperationException("fail"),
                template, []);
            var exception = Record.Exception(() => sink.Emit(logEvent));
            Assert.Null(exception);
        }

        [Fact]
        public void Emit_EmptyMessageTemplate_PassesThroughDirectly()
        {
            using var sink = new ChunkingOpenTelemetrySink(BuildOptions(), EmptyConfig());
            // An empty template produces empty string text — the sink passes it through without chunking
            var template = new MessageTemplateParser().Parse(string.Empty);
            var logEvent = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, template, []);
            Assert.Equal(string.Empty, logEvent.MessageTemplate.Text);
            var exception = Record.Exception(() => sink.Emit(logEvent));
            Assert.Null(exception);
        }

        [Fact]
        public void Emit_AfterDispose_SilentlyReturns()
        {
            var sink = new ChunkingOpenTelemetrySink(BuildOptions(), EmptyConfig());
            sink.Dispose();
            // After dispose the sink should silently return (not throw)
            var exception = Record.Exception(() => sink.Emit(BuildLogEvent("test after dispose")));
            Assert.Null(exception);
        }

        [Fact]
        public void Emit_LongMessageWithError_AllLevels()
        {
            foreach (var level in Enum.GetValues<LogEventLevel>())
            {
                using var sink = new ChunkingOpenTelemetrySink(BuildOptions(maxLogLength: 5), EmptyConfig());
                var logEvent = BuildLogEvent(new string('y', 20), level);
                var exception = Record.Exception(() => sink.Emit(logEvent));
                Assert.Null(exception);
            }
        }

        [Fact]
        public void Constructor_WithLogsExportDelayMs_ConfiguresBatchPeriod()
        {
            var opts = BuildOptions();
            opts.LogsExportDelayMs = 3000;
            // Should create sink without error, with custom batch period
            using var sink = new ChunkingOpenTelemetrySink(opts, EmptyConfig());
            Assert.NotNull(sink);
        }

        [Fact]
        public void Constructor_WithDynatraceAuthorizationHeader_DoesNotThrow()
        {
            // GetOtlpHeaders + Split(..., 2) must keep "Api-Token …" as the value
            using var sink = new ChunkingOpenTelemetrySink(
                BuildOptions(headers: "Authorization=Api-Token my-dt-token"), EmptyConfig());
            Assert.NotNull(sink);
        }

        [Fact]
        public void Constructor_WithHeaderValueContainingEquals_DoesNotThrow()
        {
            using var sink = new ChunkingOpenTelemetrySink(
                BuildOptions(headers: "api-key=abc=def=ghi"), EmptyConfig());
            Assert.NotNull(sink);
        }

        [Fact]
        public void Constructor_PrefersGetOtlpHeadersOverMismatchedOtlpHeaders()
        {
            // HeadersOverride drives GetOtlpHeaders; Otlp.Headers is intentionally different.
            var opts = BuildOptions(headers: "Authorization=Api-Token from-override");
            opts.Otlp!.Headers = "api-key=from-otlp-property";
            using var sink = new ChunkingOpenTelemetrySink(opts, EmptyConfig());
            Assert.NotNull(sink);
        }
    }
}

#endif
