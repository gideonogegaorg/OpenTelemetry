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
        private readonly IOpenTelemetryOptions _options;
        private readonly ILogger _innerLogger;
        private bool _disposed = false;

        public ChunkingOpenTelemetrySink(IOpenTelemetryOptions options, IConfiguration configuration)
        {
            // Add null checks for parameters
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));

            _options = options;

            var protocol = options.Otlp.Protocol == OtlpExportProtocol.HttpProtobuf
                ? OtlpProtocol.HttpProtobuf
                : OtlpProtocol.Grpc;

            // Add null check for headers parsing
            Dictionary<string, string> headers = null;
            if (!string.IsNullOrWhiteSpace(options.Otlp.Headers))
            {
                try
                {
                    headers = options.Otlp.Headers.Split(',')
                        .Select(part => part?.Trim().Split('='))
                        .Where(part => part?.Length == 2)
                        .ToDictionary(sp => sp[0]?.Trim(), sp => sp[1]?.Trim());
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to parse OTLP headers: {ex.Message}");
                    headers = null;
                }
            }

            // Add null check for endpoint
            var endpoint = options.LoggingEndpoint ?? options.Otlp?.Endpoint?.AbsoluteUri;
            if (string.IsNullOrWhiteSpace(endpoint))
                throw new InvalidOperationException("No logging endpoint configured");

            // Create the inner logger with OpenTelemetry sink
            _innerLogger = new LoggerConfiguration()
                .ReadSerilogConfigWithoutSinks(configuration)
                .WriteTo.OpenTelemetry(opts =>
                {
                    opts.Endpoint = endpoint;
                    opts.Protocol = protocol;
                    opts.Headers = headers;
                    opts.ResourceAttributes = options.Attributes ?? new Dictionary<string, object>();
                    opts.BatchingOptions.BatchSizeLimit = options.LogsMaxBatchSize > 0
                        ? options.LogsMaxBatchSize
                        : 100;
                    opts.BatchingOptions.QueueLimit = options.LogsMaxQueueSize > 0
                        ? options.LogsMaxQueueSize
                        : 1000;
                })
                .CreateLogger();
        }

        public void Emit(LogEvent logEvent)
        {
            // Check if disposed
            if (_disposed)
                throw new ObjectDisposedException(nameof(ChunkingOpenTelemetrySink));

            try
            {
                // Add null check for logEvent
                if (logEvent == null)
                    return;

                if (logEvent.MessageTemplate?.Text == null)
                {
                    _innerLogger.Write(logEvent);
                    return;
                }

                var message = logEvent.MessageTemplate.Text;

                if (message.Length <= _options.MaxLogMessageLength)
                {
                    _innerLogger.Write(logEvent);
                    return;
                }

                // Validate MaxLogMessageLength
                if (_options.MaxLogMessageLength <= 0)
                {
                    _innerLogger.Write(logEvent);
                    return;
                }

                // Chunk the message
                var totalChunks = (int)Math.Ceiling(message.Length / (double)_options.MaxLogMessageLength);

                for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                {
                    var start = chunkIndex * _options.MaxLogMessageLength;
                    var length = Math.Min(_options.MaxLogMessageLength, message.Length - start);
                    var chunk = message.Substring(start, length);

                    try
                    {
                        // Create a new message template for this chunk
                        var chunkTemplate = new MessageTemplateParser().Parse(chunk);

                        // Copy properties and add chunking metadata
                        var properties = new Dictionary<string, LogEventPropertyValue>(logEvent.Properties ?? new Dictionary<string, LogEventPropertyValue>());
                        properties["chunk.index"] = new ScalarValue(chunkIndex + 1);
                        properties["chunk.count"] = new ScalarValue(totalChunks);
                        properties["overflow"] = new ScalarValue(true);

                        // Create a new log event for this chunk
                        var chunkEvent = new LogEvent(
                            logEvent.Timestamp,
                            logEvent.Level,
                            chunkIndex == totalChunks - 1 ? logEvent.Exception : null,
                            chunkTemplate,
                            properties.Select(kvp => new LogEventProperty(kvp.Key, kvp.Value))
                        );

                        _innerLogger.Write(chunkEvent);
                    }
                    catch (Exception chunkEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to process chunk {chunkIndex}: {chunkEx.Message}");
                        // Continue with next chunk instead of failing completely
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in Emit: {ex.Message}");
                // Fallback: write the original event if chunking fails
                try
                {
                    if (logEvent != null)
                        _innerLogger.Write(logEvent);
                }
                catch (Exception fallbackEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Fallback write failed: {fallbackEx.Message}");
                    // Silent fail to prevent cascading errors
                }
            }
        }

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
                    (_innerLogger as IDisposable)?.Dispose();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error disposing inner logger: {ex.Message}");
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
