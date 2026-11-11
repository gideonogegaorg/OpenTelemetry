using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using OpenTelemetry.Exporter;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using Serilog.Sinks.OpenTelemetry;

namespace GMO.OpenTelemetry.Serilog
{
    public class ChunkingOpenTelemetrySink : ILogEventSink, IDisposable
    {
        private static readonly char[] HeaderKeyValueSeparator = ['='];

        /// <summary>
        /// Parses comma-separated <c>key=value</c> pairs, splitting on the first <c>=</c> so values may
        /// contain it. A repeated key keeps its last value and never fails the parse: a failed parse
        /// leaves the sink with no headers, the API key among them, and the collector rejects every log.
        /// </summary>
        internal static Dictionary<string, string> ParseHeaders(string? headers)
        {
            var parsed = new Dictionary<string, string>();
            if (Strings.IsNullOrWhiteSpace(headers))
                return parsed;

            foreach (var part in headers.Split(','))
            {
                var pair = part.Split(HeaderKeyValueSeparator, 2);
                if (pair.Length != 2)
                    continue;

                var key = pair[0].Trim();
                if (key.Length > 0)
                    parsed[key] = pair[1].Trim();
            }

            return parsed;
        }

        internal const string GrpcRequestEncodingHeader = "grpc-internal-encoding-request";

        /// <summary>
        /// Applies <see cref="IOpenTelemetryOptions.EnableGzipCompression"/> to the log sink, which has no
        /// compression setting of its own and ignores OTEL_EXPORTER_OTLP_COMPRESSION. HttpProtobuf gets a
        /// handler that gzips the body, returned for the caller to install. gRPC instead gets the request
        /// encoding header, which the sink's Grpc.Net.Client consumes to compress each message: a handler
        /// gzipping the whole gRPC body would break its framing.
        /// </summary>
        internal static HttpMessageHandler? ApplyGzipCompression(IOpenTelemetryOptions options, OtlpProtocol protocol, IDictionary<string, string> headers)
        {
            if (!options.EnableGzipCompression)
            {
                headers.Remove(GrpcRequestEncodingHeader);
                return null;
            }

            if (protocol == OtlpProtocol.Grpc)
            {
                headers[GrpcRequestEncodingHeader] = "gzip";
                return null;
            }

            return new GzipCompressingHandler();
        }

        /// <summary>
        /// The handler the sink is given, or null for its default: compression on HTTP/Protobuf, keepalive
        /// on gRPC (whose compression rides on the header <see cref="ApplyGzipCompression"/> sets).
        /// </summary>
        internal static HttpMessageHandler? CreateHttpMessageHandler(IOpenTelemetryOptions options, OtlpProtocol protocol, IDictionary<string, string> headers)
        {
            var compressingHandler = ApplyGzipCompression(options, protocol, headers);
            return protocol == OtlpProtocol.Grpc ? CreateGrpcHandler() : compressingHandler;
        }

        internal static readonly TimeSpan GrpcKeepAlivePingDelay = TimeSpan.FromSeconds(30);
        internal static readonly TimeSpan GrpcKeepAlivePingTimeout = TimeSpan.FromSeconds(10);
        internal static readonly TimeSpan GrpcPooledConnectionLifetime = TimeSpan.FromMinutes(5);

        /// <summary>
        /// The handler for the gRPC log channel. The sink sends each batch with no deadline and retries a
        /// failed batch on the same pooled HTTP/2 connection, so a connection that dies without a reset -
        /// a dropped NAT or load-balancer path - fails every retry and stops log export for the life of
        /// the process. Keepalive pings detect the dead connection and discard it, so the retry opens a
        /// new one. Null on .NET Framework, where the sink only speaks HTTP/Protobuf.
        /// </summary>
        internal static HttpMessageHandler? CreateGrpcHandler()
        {
#if NET
            return new SocketsHttpHandler
            {
                KeepAlivePingDelay = GrpcKeepAlivePingDelay,
                KeepAlivePingTimeout = GrpcKeepAlivePingTimeout,
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
                PooledConnectionLifetime = GrpcPooledConnectionLifetime,
                // What Grpc.Net.Client sets on the handler it builds itself, which this one replaces.
                EnableMultipleHttp2Connections = true,
            };
#else
            return null;
#endif
        }

        private readonly IOpenTelemetryOptions _options;
        private readonly Logger _logger;
        private readonly int _attributeBudget;

        // chunk.id, chunk.index, chunk.count and overflow, which every chunk adds.
        internal const int ChunkAttributes = 4;

        // Chunks after the first carry only scalar context this short, joined to the first by chunk.id.
        internal const int ChunkContextValueLength = NewRelicLogLimits.ChunkContextValueLength;
        private volatile bool _disposed = false;

        /// <summary>The endpoint OTLP logs will be written to, or null when none is configured.</summary>
        public static string? ResolveLoggingEndpoint(IOpenTelemetryOptions options)
        {
#if NET6_0_OR_GREATER
            ArgumentNullException.ThrowIfNull(options);
#else
            if (options == null) throw new ArgumentNullException(nameof(options));
#endif

            // Normalised again here because a custom IOpenTelemetryOptions bypasses the base setters.
            var configured = OtlpEnvironment.Blank(options.LoggingEndpoint);

            return configured ?? options.ResolveOtlpEndpoint(OtlpSignal.Logs)?.AbsoluteUri;
        }

#if !NETFRAMEWORK
        /// <summary>
        /// Mirrors <c>ResolveMetricsExportProtocol</c>: a logging endpoint on the OTLP/HTTP port or
        /// carrying <c>/v1/logs</c> implies HttpProtobuf, so an unset protocol does not open a gRPC
        /// channel against the HTTP port and fail every batch while other signals look healthy.
        /// </summary>
        private static OtlpExportProtocol ResolveLoggingProtocol(IOpenTelemetryOptions options, string endpoint)
        {
            if (Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed)
                && (parsed.Port == 4318
                    || parsed.AbsolutePath.Contains("/v1/logs", StringComparison.OrdinalIgnoreCase)))
            {
                return OtlpExportProtocol.HttpProtobuf;
            }

            return options.ResolveOtlpProtocol(OtlpSignal.Logs);
        }
#endif

        /// <summary>A sink that writes to <paramref name="inner"/> instead of an OTLP endpoint, for tests.</summary>
        internal static ChunkingOpenTelemetrySink WritingTo(IOpenTelemetryOptions options, LoggerConfiguration inner) =>
            new(options, inner.CreateLogger());

        private ChunkingOpenTelemetrySink(IOpenTelemetryOptions options, Logger logger)
        {
            _options = options;
            _attributeBudget = AttributeBudget(options);
            _logger = logger;
        }

        private static int AttributeBudget(IOpenTelemetryOptions options) =>
            LogAttributeLimiter.PropertyBudget(
                (options as OpenTelemetryOptionsBase)?.LogsMaxAttributes ?? LogAttributeLimiter.DefaultMaxAttributes,
                options.Attributes?.Count ?? 0);

        public ChunkingOpenTelemetrySink(IOpenTelemetryOptions options, IConfiguration configuration)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
#if NET6_0_OR_GREATER
            ArgumentNullException.ThrowIfNull(configuration);
#else
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
#endif

            _attributeBudget = AttributeBudget(options);

            var endpoint = ResolveLoggingEndpoint(options)
                ?? throw OtlpEnvironment.MissingEndpoint("Logs", "Telemetry:EnableLogging to false",
                    "Telemetry:LoggingEndpoint",
                    "Telemetry:Otlp:Endpoint",
                    OtlpEnvironment.Variable(OtlpSignal.Logs, "ENDPOINT"),
                    "OTEL_EXPORTER_OTLP_ENDPOINT");

            OtlpEnvironment.WarnIfSignalEndpointMismatched(
                options, OtlpSignal.Logs, endpoint, nameof(ChunkingOpenTelemetrySink));

#if !NETFRAMEWORK
            var protocol = ResolveLoggingProtocol(options, endpoint) == OtlpExportProtocol.HttpProtobuf
                ? OtlpProtocol.HttpProtobuf
                : OtlpProtocol.Grpc;
#else
            // net48: Serilog OTLP sink is HTTP/Protobuf-only.
            var protocol = OtlpProtocol.HttpProtobuf;
#endif
            // Prefer raw headers so Dynatrace Authorization=Api-Token is preserved
            // (GetApiKey strips the header name).
            var headers = ParseHeaders(options.ResolveOtlpHeadersFor(OtlpSignal.Logs));
            var handler = CreateHttpMessageHandler(options, protocol, headers);

            _logger = new LoggerConfiguration()
                .ReadSerilogConfigWithoutSinks(configuration)
                .WriteTo.OpenTelemetry(opts =>
                {
                    opts.Endpoint = endpoint;
                    opts.Protocol = protocol;
                    opts.Headers = headers;
                    if (handler != null)
                        opts.HttpMessageHandler = handler;
                    opts.ResourceAttributes = options.Attributes ?? [];
                    opts.BatchingOptions.BatchSizeLimit = options.LogsMaxBatchSize > 0
                        ? options.LogsMaxBatchSize
                        : 100;
                    opts.BatchingOptions.QueueLimit = options.LogsMaxQueueSize > 0
                        ? options.LogsMaxQueueSize
                        : 1000;
                    if (options.LogsExportDelayMs > 0)
                        opts.BatchingOptions.BufferingTimeLimit = TimeSpan.FromMilliseconds(options.LogsExportDelayMs);
                })
                .CreateLogger();
        }

        private static readonly MessageTemplateParser TemplateParser = new();

        public void Emit(LogEvent logEvent)
        {
            if (_disposed)
                return;

            try
            {
                if (logEvent == null)
                    return;

                // A report that export is failing must not depend on that export, or feed back into it.
                if (logEvent.Properties.ContainsKey(TelemetryDiagnostics.MarkerProperty))
                    return;

                // Rendered, not the raw template: chunking a template splits property tokens across
                // boundaries, and any surviving token gets re-bound and duplicated into the body.
                var message = logEvent.RenderMessage();
                var chunked = IsChunked(message);

                // Limited once, before chunking: per chunk, every chunk would carry its own copy of the
                // overflow record.
                var limited = LogAttributeLimiter.Limit(
                    logEvent,
                    chunked ? Math.Max(1, _attributeBudget - ChunkAttributes) : _attributeBudget,
                    LogAttributeLimiter.TopLevelBudget,
                    _options.MaxAttributeValueLength,
                    _options.MaxLogMessageLength,
                    message,
                    out var overflow);

                // The limiter may have frozen the message into literal text, possibly prefixed.
                if (!ReferenceEquals(limited.MessageTemplate, logEvent.MessageTemplate))
                {
                    message = limited.RenderMessage();
                    chunked = IsChunked(message);
                }

                Write(limited, chunked ? message : null);

                if (overflow != null)
                {
                    var overflowMessage = overflow.RenderMessage();
                    Write(overflow, IsChunked(overflowMessage) ? overflowMessage : null);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error in Emit");
                try
                {
                    // Non-null by construction: a null logEvent returns inside the try above.
                    _logger.Write(logEvent);
                }
                catch (Exception fallbackEx)
                {
                    _logger.Error(fallbackEx, "Fallback write failed");
                }
            }
        }

        private bool IsChunked(string message) =>
            _options.MaxLogMessageLength > 0 && message.Length > _options.MaxLogMessageLength;

        private void Write(LogEvent logEvent, string? chunkedMessage)
        {
            if (chunkedMessage == null)
            {
                _logger.Write(logEvent);
                return;
            }

            var chunks = LogMessageChunker.Chunk(chunkedMessage, _options.MaxLogMessageLength);
            var chunkId = Guid.NewGuid().ToString("N");
            for (int chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
            {
                try
                {
                    _logger.Write(BuildChunkEvent(logEvent, chunks[chunkIndex], chunkIndex, chunks.Count, chunkId));
                }
                catch (Exception chunkEx)
                {
                    _logger.Error(chunkEx, "Failed to process chunk {ChunkIndex}", chunkIndex);
                }
            }
        }

        internal static LogEvent BuildChunkEvent(LogEvent logEvent, string chunk, int chunkIndex, int chunkCount) =>
            BuildChunkEvent(logEvent, chunk, chunkIndex, chunkCount, Guid.NewGuid().ToString("N"));

        /// <summary>
        /// One chunk of an oversized event. Carries the source event's trace and span ids: the
        /// constructor without them leaves both null, detaching every chunk from its trace. The first
        /// chunk carries every property; the rest only short scalar context.
        /// </summary>
        internal static LogEvent BuildChunkEvent(LogEvent logEvent, string chunk, int chunkIndex, int chunkCount, string chunkId)
        {
            // Escaped so rendered data containing braces stays literal instead of being
            // parsed as a new property token.
            var chunkTemplate = TemplateParser.Parse(chunk.Replace("{", "{{").Replace("}", "}}"));

            var source = logEvent.Properties;
            var properties = (chunkIndex == 0 ? source : source.Where(kvp => IsChunkContext(kvp.Value)))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            properties["chunk.id"] = new ScalarValue(chunkId);
            properties["chunk.index"] = new ScalarValue(chunkIndex + 1);
            properties["chunk.count"] = new ScalarValue(chunkCount);
            properties["overflow"] = new ScalarValue(true);

            return new LogEvent(
                logEvent.Timestamp,
                logEvent.Level,
                chunkIndex == chunkCount - 1 ? logEvent.Exception : null,
                chunkTemplate,
                properties.Select(kvp => new LogEventProperty(kvp.Key, kvp.Value)),
                logEvent.TraceId ?? default,
                logEvent.SpanId ?? default);
        }

        private static bool IsChunkContext(LogEventPropertyValue value) =>
            value is ScalarValue scalar
            && (scalar.Value is not string text || text.Length <= ChunkContextValueLength);

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                try
                {
                    _logger.Dispose();
                }
                catch (Exception ex)
                {
                    GmoTelemetryEventSource.Log.OperationFailed(nameof(ChunkingOpenTelemetrySink),
                        "disposing logger", GmoTelemetryEventSource.Describe(ex));
                }
            }

            _disposed = true;
        }

        ~ChunkingOpenTelemetrySink()
        {
            Dispose(false);
        }
    }
}
