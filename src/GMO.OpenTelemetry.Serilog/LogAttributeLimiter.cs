using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Serilog.Events;
using Serilog.Formatting.Json;
using Serilog.Parsing;

namespace GMO.OpenTelemetry.Serilog
{
    /// <summary>
    /// Keeps a log event within what New Relic stores whole: at most 255 attributes per record, 128 of
    /// them top-level, arrays of at most 64 entries, and a size that keeps an export request under 1 MB.
    /// </summary>
    /// <remarks>
    /// Counted the way New Relic stores the record: every non-null scalar leaf of a destructured object or
    /// dictionary is an attribute, a collection is one array attribute whatever it holds, a null is not
    /// stored, and a string holding a JSON object - or a message that is one - becomes one attribute per
    /// field, because New Relic parses it. Over budget a record is dropped whole; over the top-level cap,
    /// arbitrary attributes are discarded. Both are reported only as an <c>NrIntegrationError</c>.
    /// <para>
    /// What does not fit moves to one overflow record: structured properties are trimmed largest first,
    /// then properties neither the template names nor the enrichers add move out. Arrays over 64 entries
    /// and strings over the chunk size stay in shortened form, their full value moving out. The overflow
    /// record holds the moved values as JSON in its message, after a text prefix so New Relic does not
    /// expand it, shares the parent's trace and span ids, and is joined to it by
    /// <see cref="OverflowIdProperty"/>. Only this sink sees any of this: every other sink on the logger
    /// gets the event unchanged.
    /// </para>
    /// <para>
    /// A property the template names cannot change without changing the message, so when one must, the
    /// message is first frozen into literal text. The message then carries the full value, and the
    /// property is shortened in place rather than moved.
    /// </para>
    /// </remarks>
    internal static class LogAttributeLimiter
    {
        internal const int DefaultMaxAttributes = OpenTelemetryOptionsBase.DefaultLogsMaxAttributes;

        internal const string CollapsedProperty = "attribute_limit.collapsed";
        internal const string OriginalCountProperty = "attribute_limit.original";
        internal const string OverflowIdProperty = "attribute_limit.overflow_id";
        internal const string OverflowKeysProperty = "attribute_limit.overflow_keys";

        // New Relic expands a message that is a whole JSON object into one attribute per field, which
        // would put every moved value back over the limit and drop the overflow record. The prefix keeps
        // the message text.
        internal const string OverflowMessagePrefix = "Attributes over the limit: ";

        internal const string JsonMessagePrefix = NewRelicLogLimits.JsonMessagePrefix;

        // exception.type/.message/.stacktrace and message_template.text/.hash.md5/.renderings, which the
        // sink adds itself.
        internal const int SinkAttributes = 6;

        // Added to every record on ingest: entity.guid/.guids/.name/.type, instrumentation.provider,
        // newrelic.source/.logs.metadata/.logPattern/.logs.batchIndex, messageId, timestamp, message,
        // severity.text/.number, otel.library.name/.version, telemetry.sdk.language/.name/.version,
        // trace.id and span.id.
        internal const int NewRelicAttributes = 21;

        // Collapsed, original count and overflow id.
        internal const int MarkerAttributes = 3;

        // New Relic also caps a log record at 128 top-level OTLP attributes, counting a destructured
        // object once. Over it the record is kept but arbitrary attributes are discarded, markers
        // included, reported only as an NrIntegrationError that the record was modified.
        internal const int MaxRecordAttributes = 128;

        internal const int TopLevelBudget = MaxRecordAttributes - SinkAttributes - MarkerAttributes - ChunkingOpenTelemetrySink.ChunkAttributes;

        internal const int MaxArrayLength = NewRelicLogLimits.MaxArrayLength;

        // What the enrichers add for finding and joining a record. Moving it out would leave the parent
        // record unsearchable by the attributes every query filters on.
        private static readonly HashSet<string> Context = new(StringComparer.Ordinal)
        {
            "level", "SourceContext", "CorrelationId", "trace.id", "span.id", "operation.name",
            "thread.id", "thread.name", "user.identity", "app.domain",
            "source.class", "source.method", "source.file", "source.line",
        };

        private static readonly JsonValueFormatter Json = new(typeTagName: null);
        private static readonly MessageTemplateParser TemplateParser = new();

        /// <summary>The budget left for event properties once fixed and resource attributes are taken.</summary>
        internal static int PropertyBudget(int maxAttributes, int resourceAttributes)
        {
            var max = maxAttributes > 0 ? maxAttributes : DefaultMaxAttributes;
            return Math.Max(1, max - SinkAttributes - NewRelicAttributes - MarkerAttributes - Math.Max(0, resourceAttributes));
        }

        internal static int CountAttributes(LogEventPropertyValue value)
        {
            switch (value)
            {
                case StructureValue structure:
                    // Indexed, not foreach: this runs on every event, and enumerating the interface
                    // allocates.
                    var members = 0;
                    var properties = structure.Properties;
                    for (var i = 0; i < properties.Count; i++)
                        members += CountAttributes(properties[i].Value);
                    return members;
                case DictionaryValue dictionary:
                    var entries = 0;
                    foreach (var element in dictionary.Elements)
                        entries += CountAttributes(element.Value);
                    return entries;
                case ScalarValue scalar:
                    if (scalar.Value == null)
                        return 0;
                    return scalar.Value is string text && JsonObjectFields(text) is int fields ? fields : 1;
                default:
                    return 1;
            }
        }

        internal static int? JsonObjectFields(string text) => NewRelicLogLimits.JsonObjectFields(text);

        /// <summary>
        /// Returns <paramref name="logEvent"/> itself when its properties fit <paramref name="budget"/>
        /// and <see cref="TopLevelBudget"/>, otherwise a copy whose properties do, with what was moved out
        /// in <paramref name="overflow"/>.
        /// </summary>
        internal static LogEvent Limit(LogEvent logEvent, int budget, int maxValueLength, out LogEvent? overflow) =>
            Limit(logEvent, budget, TopLevelBudget, maxValueLength, 0, null, out overflow);

        /// <param name="maxStringLength">Strings longer than this keep <paramref name="maxValueLength"/>
        /// characters and move whole to the overflow record; zero or less leaves them alone.</param>
        /// <param name="renderedMessage">The message already rendered, or null to render it here.</param>
        internal static LogEvent Limit(
            LogEvent logEvent,
            int budget,
            int topLevelBudget,
            int maxValueLength,
            int maxStringLength,
            string? renderedMessage,
            out LogEvent? overflow)
        {
            overflow = null;

            var limits = new Limits(budget, topLevelBudget, maxValueLength, maxStringLength);
            var original = Count(logEvent.Properties);
            var messageFields = MessageFields(logEvent, ref renderedMessage);
            var mustClip = logEvent.Properties.Any(p => NeedsClip(p.Value, limits));

            if (original + messageFields <= budget && logEvent.Properties.Count <= topLevelBudget && !mustClip)
                return logEvent;

            var inTemplate = TemplateNames(logEvent.MessageTemplate);

            // A template-named property that must change, or a JSON message that does not fit, can only
            // be handled once the message no longer depends on the properties. Otherwise the first pass
            // leaves template-named properties alone, and the message is frozen only if that cannot fit.
            var jsonOver = messageFields > 0 && original + messageFields > budget;
            var mustFreeze = jsonOver || logEvent.Properties.Any(p => inTemplate.Contains(p.Key) && NeedsClip(p.Value, limits));
            var work = Attempt(logEvent, renderedMessage, messageFields, limits, inTemplate, mustFreeze, jsonOver);
            if (!work.Fits(limits) && !mustFreeze && inTemplate.Count > 0)
                work = Attempt(logEvent, renderedMessage, messageFields, limits, inTemplate, freeze: true, prefixJson: messageFields > 0);

            work.Properties[CollapsedProperty] = new ScalarValue(true);
            work.Properties[OriginalCountProperty] = new ScalarValue(original + messageFields);

            string? overflowId = null;
            if (work.Moved.Count > 0)
            {
                overflowId = Guid.NewGuid().ToString("N");
                work.Properties[OverflowIdProperty] = new ScalarValue(overflowId);
            }

            var limited = new LogEvent(
                logEvent.Timestamp,
                logEvent.Level,
                logEvent.Exception,
                work.Template,
                work.Properties.Select(p => new LogEventProperty(p.Key, p.Value)),
                logEvent.TraceId ?? default,
                logEvent.SpanId ?? default);

            if (overflowId != null)
                overflow = Overflow(limited, work.Moved);

            return limited;
        }

        private static Work Attempt(
            LogEvent logEvent,
            string? renderedMessage,
            int messageFields,
            Limits limits,
            HashSet<string> inTemplate,
            bool freeze,
            bool prefixJson)
        {
            var work = new Work(logEvent, renderedMessage, messageFields);
            var fixedNames = inTemplate;
            if (freeze)
            {
                work.Freeze(prefixJson);
                fixedNames = new HashSet<string>(StringComparer.Ordinal);
            }

            ClipAll(work, limits);
            work.Total = Count(work.Properties) + work.JsonFields;
            work.Total = TrimStructured(work, work.Total, limits, fixedNames);
            if (work.Total > limits.Budget || work.Properties.Count > limits.TopLevel)
                work.Total = MovePlain(work, work.Total, limits, fixedNames);
            return work;
        }

        private static int MessageFields(LogEvent logEvent, ref string? renderedMessage)
        {
            // Cheap test first: only a template that can render as a JSON object is rendered and parsed.
            var text = logEvent.MessageTemplate.Text;
            if (string.IsNullOrEmpty(text) || text.TrimStart().FirstOrDefault() != '{')
                return 0;

            renderedMessage ??= logEvent.RenderMessage();
            return JsonObjectFields(renderedMessage) ?? 0;
        }

        private static HashSet<string> TemplateNames(MessageTemplate template) =>
            new(template.Tokens.OfType<PropertyToken>().Select(t => t.PropertyName), StringComparer.Ordinal);

        /// <summary>Shortens arrays and long strings; the full value of a non-template property moves out.</summary>
        private static void ClipAll(Work work, Limits limits)
        {
            foreach (var key in work.Properties.Keys.ToList())
            {
                var value = work.Properties[key];
                var clipped = Clip(value, limits, out var lostData);
                if (ReferenceEquals(clipped, value))
                    continue;

                work.Properties[key] = clipped;

                // After a freeze the message holds the template-named values in full, so nothing is lost.
                // Before one, a template-named value that needs clipping has forced the freeze.
                if (lostData && !work.Frozen.Contains(key))
                    work.Moved[key] = value;
            }
        }

        private static bool NeedsClip(LogEventPropertyValue value, Limits limits) =>
            !ReferenceEquals(Clip(value, limits, out _), value);

        /// <summary>
        /// Arrays cut to <see cref="MaxArrayLength"/> without null entries, and strings over the limit
        /// shortened, at any depth. Returns <paramref name="value"/> itself when nothing changes.
        /// </summary>
        private static LogEventPropertyValue Clip(LogEventPropertyValue value, Limits limits, out bool lostData)
        {
            lostData = false;
            switch (value)
            {
                case ScalarValue { Value: string text } when limits.MaxStringLength > 0 && text.Length > limits.MaxStringLength:
                    lostData = true;
                    return new ScalarValue(Truncate(text, limits.MaxValueLength));
                case SequenceValue sequence:
                    return ClipSequence(sequence, limits, out lostData);
                case StructureValue structure:
                    return ClipStructure(structure, limits, out lostData);
                case DictionaryValue dictionary:
                    return ClipDictionary(dictionary, limits, out lostData);
                default:
                    return value;
            }
        }

        private static StructureValue ClipStructure(StructureValue structure, Limits limits, out bool lostData)
        {
            lostData = false;
            var changed = false;
            var members = new List<LogEventProperty>(structure.Properties.Count);
            foreach (var member in structure.Properties)
            {
                var clipped = Clip(member.Value, limits, out var lost);
                lostData |= lost;
                var same = ReferenceEquals(clipped, member.Value);
                changed |= !same;
                members.Add(same ? member : new LogEventProperty(member.Name, clipped));
            }
            return changed ? new StructureValue(members, structure.TypeTag) : structure;
        }

        private static DictionaryValue ClipDictionary(DictionaryValue dictionary, Limits limits, out bool lostData)
        {
            lostData = false;
            var changed = false;
            var elements = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>();
            foreach (var element in dictionary.Elements)
            {
                var clipped = Clip(element.Value, limits, out var lost);
                lostData |= lost;
                changed |= !ReferenceEquals(clipped, element.Value);
                elements.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(element.Key, clipped));
            }
            return changed ? new DictionaryValue(elements) : dictionary;
        }

        private static SequenceValue ClipSequence(SequenceValue sequence, Limits limits, out bool lostData)
        {
            lostData = false;
            var changed = false;
            var kept = new List<LogEventPropertyValue>(Math.Min(sequence.Elements.Count, MaxArrayLength));
            foreach (var element in sequence.Elements)
            {
                // New Relic drops a null entry, losing nothing.
                if (element is ScalarValue { Value: null })
                {
                    changed = true;
                    continue;
                }

                if (kept.Count == MaxArrayLength)
                {
                    lostData = changed = true;
                    break;
                }

                var clipped = Clip(element, limits, out var lost);
                lostData |= lost;
                changed |= !ReferenceEquals(clipped, element);
                kept.Add(clipped);
            }

            return changed ? new SequenceValue(kept) : sequence;
        }

        /// <summary>Trims structured properties largest first until the total fits; returns the new total.</summary>
        private static int TrimStructured(Work work, int total, Limits limits, HashSet<string> inTemplate)
        {
            var structured = work.Properties
                .Where(p => (p.Value is StructureValue || p.Value is DictionaryValue) && !inTemplate.Contains(p.Key))
                .Select(p => new { p.Key, Count = CountAttributes(p.Value) })
                .Where(p => p.Count > 1)
                .OrderByDescending(p => p.Count)
                .ToList();

            foreach (var candidate in structured)
            {
                if (total <= limits.Budget)
                    break;

                var current = work.Properties[candidate.Key];
                var kept = Trim(current, candidate.Count - (total - limits.Budget), out var removed);
                if (kept == null)
                {
                    removed = current;
                    kept = new ScalarValue(Truncate(current.ToString(), limits.MaxValueLength));
                }

                work.Properties[candidate.Key] = kept;
                // A property already clipped leaves whole: its full value is what was moved for it. A
                // frozen message already holds the whole value.
                if (!work.Moved.ContainsKey(candidate.Key) && !work.Frozen.Contains(candidate.Key))
                    work.Moved[candidate.Key] = removed;
                total -= candidate.Count - CountAttributes(kept);
            }

            return total;
        }

        /// <summary>
        /// Moves properties that neither the message template names nor the enrichers add: largest first
        /// while the total is over the budget, then smallest first while the count is over the top-level
        /// budget, so the structured values left keep their members. Returns the new total.
        /// </summary>
        private static int MovePlain(Work work, int total, Limits limits, HashSet<string> inTemplate)
        {
            var plain = work.Properties.Keys
                .Where(k => !inTemplate.Contains(k) && !Context.Contains(k))
                .Select(k => new { Key = k, Count = CountAttributes(work.Properties[k]) })
                .OrderByDescending(p => p.Count)
                .ToList();

            void Move(string key, int count)
            {
                // A frozen message already holds the value in full; anything else leaves whole, its
                // original value replacing any part already moved.
                if (work.Frozen.Contains(key))
                    work.Moved.Remove(key);
                else
                    work.Moved[key] = work.Original.TryGetValue(key, out var original) ? original : work.Properties[key];
                work.Properties.Remove(key);
                total -= count;
            }

            var largest = 0;
            var smallest = plain.Count - 1;
            while (total > limits.Budget && largest <= smallest)
            {
                Move(plain[largest].Key, plain[largest].Count);
                largest++;
            }

            while (work.Properties.Count > limits.TopLevel && largest <= smallest)
            {
                Move(plain[smallest].Key, plain[smallest].Count);
                smallest--;
            }

            return total;
        }

        private static int Count(IReadOnlyDictionary<string, LogEventPropertyValue> properties)
        {
            var total = 0;
            // LogEvent keeps its properties in a Dictionary; enumerating it as one avoids boxing the
            // enumerator on every event.
            if (properties is Dictionary<string, LogEventPropertyValue> dictionary)
            {
                foreach (var property in dictionary)
                    total += CountAttributes(property.Value);
            }
            else
            {
                foreach (var property in properties)
                    total += CountAttributes(property.Value);
            }
            return total;
        }

        /// <summary>
        /// Splits a structure or dictionary into the members that fit <paramref name="allowed"/>, in their
        /// original order, and the rest. Returns null when no member with a value fits.
        /// </summary>
        private static LogEventPropertyValue? Trim(LogEventPropertyValue value, int allowed, out LogEventPropertyValue removed)
        {
            var used = 0;

            if (value is StructureValue structure)
            {
                var kept = new List<LogEventProperty>();
                var rest = new List<LogEventProperty>();
                foreach (var property in structure.Properties)
                {
                    var count = CountAttributes(property.Value);
                    if (used + count <= allowed)
                    {
                        kept.Add(property);
                        used += count;
                    }
                    else
                    {
                        rest.Add(property);
                    }
                }

                removed = new StructureValue(rest, structure.TypeTag);
                return used > 0 ? new StructureValue(kept, structure.TypeTag) : null;
            }

            var dictionary = (DictionaryValue)value;
            var keptEntries = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>();
            var restEntries = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>();
            foreach (var element in dictionary.Elements)
            {
                var count = CountAttributes(element.Value);
                if (used + count <= allowed)
                {
                    keptEntries.Add(element);
                    used += count;
                }
                else
                {
                    restEntries.Add(element);
                }
            }

            removed = new DictionaryValue(restEntries);
            return used > 0 ? new DictionaryValue(keptEntries) : null;
        }

        private static LogEvent Overflow(LogEvent parent, Dictionary<string, LogEventPropertyValue> moved)
        {
            string json;
            using (var writer = new StringWriter())
            {
                Json.Format(new StructureValue(moved.Select(m => new LogEventProperty(m.Key, m.Value))), writer);
                json = writer.ToString();
            }

            // The parent's scalar context - ids, application, correlation - so the record is findable on
            // its own; not what was moved, and not the parent-only markers.
            var properties = parent.Properties
                .Where(p => p.Value is ScalarValue && !moved.ContainsKey(p.Key) && p.Key != CollapsedProperty && p.Key != OriginalCountProperty)
                .Select(p => new LogEventProperty(p.Key, p.Value))
                .ToList();
            properties.Add(new LogEventProperty(OverflowKeysProperty, new ScalarValue(string.Join(",", moved.Keys))));

            return new LogEvent(
                parent.Timestamp,
                parent.Level,
                null,
                Literal(OverflowMessagePrefix + json),
                properties,
                parent.TraceId ?? default,
                parent.SpanId ?? default);
        }

        // Braces are escaped so the text stays literal instead of being parsed as property tokens.
        private static MessageTemplate Literal(string text) =>
            TemplateParser.Parse(text.Replace("{", "{{").Replace("}", "}}"));

        private static string Truncate(string value, int maxLength) =>
            maxLength > 0 && value.Length > maxLength ? value[..maxLength] : value;

        private readonly struct Limits(int budget, int topLevelBudget, int maxValueLength, int maxStringLength)
        {
            public int Budget { get; } = budget;
            public int TopLevel { get; } = topLevelBudget;
            public int MaxValueLength { get; } = maxValueLength;
            public int MaxStringLength { get; } = maxStringLength;
        }

        /// <summary>The event being limited: its template, its properties and what has moved out.</summary>
        private sealed class Work(LogEvent logEvent, string? rendered, int messageFields)
        {
            private string? _rendered = rendered;

            public LogEvent Event { get; } = logEvent;
            public MessageTemplate Template { get; private set; } = logEvent.MessageTemplate;
            public Dictionary<string, LogEventPropertyValue> Properties { get; } = logEvent.Properties.ToDictionary(p => p.Key, p => p.Value);
            public IReadOnlyDictionary<string, LogEventPropertyValue> Original { get; } = logEvent.Properties;
            public Dictionary<string, LogEventPropertyValue> Moved { get; } = [];

            // Properties the template named before it was frozen: the message holds their full value.
            public HashSet<string> Frozen { get; } = new HashSet<string>(StringComparer.Ordinal);

            public int JsonFields { get; private set; } = messageFields;

            public int Total { get; set; }

            public bool Fits(Limits limits) => Total <= limits.Budget && Properties.Count <= limits.TopLevel;

            /// <summary>Replaces the template with the message it renders, so properties can change freely.</summary>
            public void Freeze(bool prefixJson)
            {
                if (Frozen.Count == 0)
                {
                    foreach (var name in TemplateNames(Template))
                        Frozen.Add(name);
                }

                var text = _rendered ??= Event.RenderMessage();
                JsonFields = JsonObjectFields(text) ?? 0;
                if (prefixJson && JsonFields > 0)
                {
                    text = JsonMessagePrefix + text;
                    JsonFields = 0;
                }

                _rendered = text;
                Template = Literal(text);
            }
        }
    }
}
