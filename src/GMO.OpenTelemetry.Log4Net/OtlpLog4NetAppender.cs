using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using log4net.Appender;
using log4net.Core;
using log4net.Util;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace GMO.OpenTelemetry.Log4Net
{
    public class OtlpLog4NetAppender(
        ILoggerFactory loggerFactory,
        IAttributeEnricher attributeEnricher,
        IOpenTelemetryOptions openTelemetryOptions,
        string? categoryName = null) : AppenderSkeleton
    {
        // Exactly what this appender exports. Not FixFlags.All: that adds UserName, which resolves a
        // WindowsIdentity per event and is never read here.
        private const FixFlags RequiredFixFlags =
            FixFlags.Partial | FixFlags.LocationInfo | FixFlags.Identity;

        private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

        private readonly ILogger _logger = loggerFactory.CreateLogger(categoryName ?? typeof(OtlpLog4NetAppender).FullName!);
        private readonly IAttributeEnricher _attributeEnricher = attributeEnricher;
        private readonly IOpenTelemetryOptions _openTelemetryOptions = openTelemetryOptions;

        protected override void Append(LoggingEvent loggingEvent)
        {
            if (!_openTelemetryOptions.Enabled) return;

            // A report that export is failing must not depend on that export, or feed back into it.
            if (loggingEvent.LookupProperty(TelemetryDiagnostics.MarkerProperty) != null) return;

            try
            {
                // Confined to the Fix="0" case: Fix mutates the event log4net shares with every appender.
                if (string.IsNullOrEmpty(loggingEvent.RenderedMessage) && HasRecoverableMessage(loggingEvent))
                {
                    try
                    {
                        loggingEvent.Fix = RequiredFixFlags;
                    }
                    catch (Exception fixEx)
                    {
                        Report("could not fix event data", fixEx);
                    }
                }

                var max = _openTelemetryOptions.MaxLogMessageLength;
                var logLevel = MapLevel(loggingEvent.Level);
                var chunks = LogMessageChunker.Chunk(loggingEvent.RenderedMessage, max);
                var messageObject = SerializeMessageObject(loggingEvent, max > 0 ? max - chunks[0].Length : int.MaxValue);
                var exceptionText = GetExceptionTextIfCached(loggingEvent);

                var chunkId = chunks.Count > 1 ? Guid.NewGuid().ToString("N") : null;
                for (int i = 0; i < chunks.Count; i++)
                {
                    try
                    {
                        EmitChunk(chunks[i], logLevel, loggingEvent, new ChunkPosition(i, chunks.Count, chunkId),
                                i == 0 ? messageObject : null, exceptionText);
                    }
                    catch (Exception chunkEx)
                    {
                        Report($"error emitting chunk {i + 1}/{chunks.Count}", chunkEx);
                    }
                }
            }
            catch (Exception ex)
            {
                Report("error in Append", ex);
            }
        }

        // ErrorHandler is throttled so a 100%-failure mode cannot flood Console.Error from the logging
        // hot path; the EventSource stays visible after that first error. Neither re-enters log4net.
        private void Report(string operation, Exception ex)
        {
            ErrorHandler.Error($"OtlpLog4NetAppender: {operation}", ex);
            GmoTelemetryEventSource.Log.OperationFailed(nameof(OtlpLog4NetAppender), operation,
                    GmoTelemetryEventSource.Describe(ex));
        }

        // GetExceptionString() caches "" into the shared event's data, so it is only called for a fixed
        // event, where it is the sole surviving form. An unfixed event with no ExceptionObject has none.
        private static string? GetExceptionTextIfCached(LoggingEvent loggingEvent) =>
            loggingEvent.ExceptionObject == null && loggingEvent.Fix != FixFlags.None
                ? loggingEvent.GetExceptionString()
                : null;

        // log.Info("") is blank with nothing to recover, so it must not trigger a re-fix.
        private static bool HasRecoverableMessage(LoggingEvent loggingEvent)
        {
            var messageObject = loggingEvent.MessageObject;
            return messageObject != null && !(messageObject is string s && s.Length == 0);
        }

        private static LogLevel MapLevel(Level? level)
        {
            if (level == null) return LogLevel.Information;
            if (level >= Level.Error) return LogLevel.Error;
            if (level >= Level.Warn) return LogLevel.Warning;
            if (level >= Level.Info) return LogLevel.Information;
            if (level >= Level.Debug) return LogLevel.Debug;
            return LogLevel.Trace;
        }

        // Bounded by what the first chunk leaves of the cap, since both ride the same record. Bounding it
        // independently would allow a chunk plus a messageObject of nearly twice the limit.
        private static string? SerializeMessageObject(LoggingEvent loggingEvent, int budget)
        {
            var serialized = SerializeMessageObjectCore(loggingEvent);
            if (serialized == null || budget <= 0) return null;
            if (serialized.Length <= budget) return serialized;

            return serialized[..LogMessageChunker.FirstChunkLength(serialized, budget)];
        }

        private static string? SerializeMessageObjectCore(LoggingEvent loggingEvent)
        {
            var messageObject = loggingEvent.MessageObject;
            if (messageObject == null || messageObject is string) return null;

            // An EF entity is never walked: its getters can lazy-load, and its navigation graph is unbounded.
            // Nor is a query: serializing one enumerates it, which runs it against the database.
            if (IsSkipped(messageObject.GetType())) return messageObject.GetType().FullName;

            try
            {
                return JsonSerializer.Serialize(messageObject, SerializerOptions);
            }
            catch
            {
                try
                {
                    return messageObject.ToString();
                }
                catch
                {
                    return null;
                }
            }
        }

        // The same EF match the span renderer uses, so a type skipped in traces is skipped here too.
        private static bool IsSkipped(Type type) =>
            ObjectRenderer.IsEntityFrameworkType(type) || typeof(IQueryable).IsAssignableFrom(type);

        // Navigation properties and queries are dropped from the contract, so a plain DTO that holds an
        // entity or a query serializes without its getters ever being invoked.
        private static void SkipEntityFrameworkMembers(JsonTypeInfo typeInfo)
        {
            if (typeInfo.Kind != JsonTypeInfoKind.Object) return;

            if (ObjectRenderer.IsEntityFrameworkType(typeInfo.Type))
            {
                typeInfo.Properties.Clear();
                return;
            }

            for (var i = typeInfo.Properties.Count - 1; i >= 0; i--)
            {
                var propertyType = typeInfo.Properties[i].PropertyType;
                var element = propertyType.IsGenericType ? propertyType.GetGenericArguments()[0] : null;
                if (IsSkipped(propertyType) || (element != null && ObjectRenderer.IsEntityFrameworkType(element)))
                    typeInfo.Properties.RemoveAt(i);
            }
        }

        private static JsonSerializerOptions CreateSerializerOptions() => new()
        {
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            MaxDepth = 4,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers = { SkipEntityFrameworkMembers },
            },
        };

        private readonly struct ChunkPosition(int index, int count, string? id)
        {
            public int Index { get; } = index;
            public int Count { get; } = count;
            public string? Id { get; } = id;
        }

        private void EmitChunk(string chunk, LogLevel logLevel, LoggingEvent loggingEvent, ChunkPosition position,
                string? messageObject, string? exceptionText)
        {
            var chunkIndex = position.Index;
            var totalChunks = position.Count;
            var chunkId = position.Id;
            var state = _attributeEnricher.Enrich() ?? [];

            state["level"] = loggingEvent.Level?.Name ?? string.Empty;
            state["logger.name"] = loggingEvent.LoggerName ?? string.Empty;

            if (!Strings.IsNullOrEmpty(loggingEvent.ThreadName))
                state["thread.name"] = loggingEvent.ThreadName;

            if (messageObject != null)
                state["messageObject"] = messageObject;

            var isLastChunk = chunkIndex == totalChunks - 1;
            if (isLastChunk && !Strings.IsNullOrEmpty(exceptionText))
                state["exception"] = exceptionText;

            AddLocationInformation(state, loggingEvent.LocationInformation);
            AddLog4NetProperties(state, loggingEvent, chunkIndex == 0, _openTelemetryOptions);

            if (totalChunks > 1)
            {
                state["chunk.id"] = chunkId!;
                state["chunk.index"] = chunkIndex + 1;
                state["chunk.count"] = totalChunks;
                state["overflow"] = true;
            }

            var message = FitJson(state, chunk, AttributeBudget(_openTelemetryOptions));
            state["message"] = message;

            _logger.Log(
                    logLevel,
                    new EventId(),
                    state,
                    isLastChunk ? loggingEvent.ExceptionObject : null,
                    (st, ex) => message
            );
        }

        // What a record may carry before New Relic drops it: the configured total less what New Relic
        // adds on ingest (about 21), the exception attributes and the resource attributes.
        internal static int AttributeBudget(IOpenTelemetryOptions options)
        {
            var max = (options as OpenTelemetryOptionsBase)?.LogsMaxAttributes ?? 0;
            if (max <= 0)
                max = OpenTelemetryOptionsBase.DefaultLogsMaxAttributes;
            return Math.Max(1, max - 21 - 3 - (options.Attributes?.Count ?? 0));
        }

        /// <summary>
        /// New Relic expands a message or string value that is a JSON object into one attribute per field
        /// and drops a record that ends up over the limit. When this one would, JSON values are prefixed,
        /// largest first, so they stay text. Returns the message to send.
        /// </summary>
        internal static string FitJson(Dictionary<string, object> state, string message, int budget)
        {
            var messageFields = NewRelicLogLimits.JsonObjectFields(message) ?? 0;
            var jsonValues = state
                .Where(kvp => kvp.Key != "message" && kvp.Value is string)
                .Select(kvp => new { kvp.Key, Fields = NewRelicLogLimits.JsonObjectFields((string)kvp.Value) ?? 0 })
                .Where(v => v.Fields > 0)
                .ToList();

            // The message counts once whatever its length, as message and body are one attribute.
            var total = state.Count + 1 + messageFields + jsonValues.Sum(v => v.Fields - 1);
            if (total <= budget)
                return message;

            if (messageFields > 0)
            {
                message = NewRelicLogLimits.JsonMessagePrefix + message;
                total -= messageFields;
            }

            foreach (var value in jsonValues.OrderByDescending(v => v.Fields))
            {
                if (total <= budget)
                    break;
                state[value.Key] = NewRelicLogLimits.JsonMessagePrefix + (string)state[value.Key];
                total -= value.Fields - 1;
            }

            return message;
        }

        private static void AddLocationInformation(Dictionary<string, object> state, LocationInfo? locationInfo)
        {
            if (locationInfo == null) return;

            if (!Strings.IsNullOrEmpty(locationInfo.ClassName))
                state["source.class"] = locationInfo.ClassName;
            if (!Strings.IsNullOrEmpty(locationInfo.MethodName))
                state["source.method"] = locationInfo.MethodName;
            if (!Strings.IsNullOrEmpty(locationInfo.FileName))
                state["source.file"] = locationInfo.FileName;
            if (!Strings.IsNullOrEmpty(locationInfo.LineNumber))
                state["source.line"] = locationInfo.LineNumber;
        }

        // Chunks after the first carry only short values: every chunk copying a large one multiplies it
        // into the export request, which New Relic rejects whole over 1 MB. Values longer than a chunk
        // are cut to the attribute length for the same reason.
        private static void AddLog4NetProperties(Dictionary<string, object> state, LoggingEvent loggingEvent,
                bool firstChunk, IOpenTelemetryOptions options)
        {
            if (!Strings.IsNullOrEmpty(loggingEvent.Identity))
                state["user.identity"] = loggingEvent.Identity;

            if (!Strings.IsNullOrEmpty(loggingEvent.Domain))
                state["app.domain"] = loggingEvent.Domain;

            if (loggingEvent.Properties == null || loggingEvent.Properties.Count <= 0) return;

            foreach (var property in loggingEvent.Properties)
            {
                // No collision filter: the property. prefix makes the target key unreachable by any
                // attribute this appender or the enricher writes, so filtering could only drop data.
                var key = property.Key;
                if (string.IsNullOrEmpty(key) || key.StartsWith("log4net:"))
                    continue;

                var value = property.Value?.ToString() ?? string.Empty;
                if (!firstChunk && value.Length > NewRelicLogLimits.ChunkContextValueLength)
                    continue;
                if (options.MaxLogMessageLength > 0 && value.Length > options.MaxLogMessageLength && options.MaxAttributeValueLength > 0)
                    value = value[..LogMessageChunker.FirstChunkLength(value, options.MaxAttributeValueLength)];

                state[$"property.{key}"] = value;
            }
        }
    }
}
