using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using GMO.OpenTelemetry.Serilog;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class LogAttributeLimiterTests
    {
        private static readonly string[] BigKey = { "\"Big\"" };

        private static readonly MessageTemplateParser Parser = new MessageTemplateParser();

        [Fact]
        public void UnderBudget_ReturnsTheSameEventAndNoOverflow()
        {
            var logEvent = Event("Hello {Name}", new LogEventProperty("Name", new ScalarValue("x")), Prop("Other", Structure(10)));

            Assert.Same(logEvent, LogAttributeLimiter.Limit(logEvent, 20, 4000, out var overflow));
            Assert.Null(overflow);
        }

        [Fact]
        public void CountAttributes_CountsAsNewRelicStoresTheRecord()
        {
            var nested = new StructureValue(new[]
            {
                new LogEventProperty("A", new ScalarValue(1)),
                new LogEventProperty("List", new SequenceValue(new[] { Structure(30), Structure(40) })),
                new LogEventProperty("Map", new DictionaryValue(new[]
                {
                    new KeyValuePair<ScalarValue, LogEventPropertyValue>(new ScalarValue("k1"), new ScalarValue(1)),
                    new KeyValuePair<ScalarValue, LogEventPropertyValue>(new ScalarValue("k2"), Structure(2)),
                })),
                new LogEventProperty("Null", new ScalarValue(null)),
                new LogEventProperty("Empty", new SequenceValue(Array.Empty<LogEventPropertyValue>())),
            });

            // A, the list as one array, the map's three leaves and the empty list; the null is not stored.
            Assert.Equal(1 + 1 + 3 + 1, LogAttributeLimiter.CountAttributes(nested));
        }

        [Fact]
        public void EmployeeWithALargeDocumentList_IsNotCollapsed()
        {
            // ~150 fields plus 9 documents of ~200 fields each, which New Relic stores with the documents
            // as one array attribute: well under the limit, though the leaves number nearly 2,000.
            var documents = new SequenceValue(Enumerable.Range(0, 9).Select(_ => Structure(200)));
            var employee = new StructureValue(Enumerable.Range(0, 150)
                .Select(i => new LogEventProperty($"Field{i}", new ScalarValue(i)))
                .Append(new LogEventProperty("OnboardingDocuments", documents)));
            var logEvent = Event("Onboarding initiated for AgencyId: {AgencyId}, Employee: {@EmployeeTraceObject}",
                Prop("AgencyId", new ScalarValue(1055042)), Prop("EmployeeTraceObject", employee));
            var budget = LogAttributeLimiter.PropertyBudget(LogAttributeLimiter.DefaultMaxAttributes, 6);

            Assert.Same(logEvent, LogAttributeLimiter.Limit(logEvent, budget, 4000, out var overflow));
            Assert.Null(overflow);
        }

        [Fact]
        public void OverBudget_TrimsTheLargestStructureAndMovesItsRestToTheOverflow()
        {
            var logEvent = Event("Saved", Prop("Big", Structure(150)), Prop("Small", Structure(60)));

            var limited = LogAttributeLimiter.Limit(logEvent, 100, 4000, out var overflow);

            var big = Assert.IsType<StructureValue>(limited.Properties["Big"]);
            Assert.Equal(Enumerable.Range(0, 40).Select(i => $"F{i}"), big.Properties.Select(p => p.Name));
            Assert.Equal(60, ((StructureValue)limited.Properties["Small"]).Properties.Count);
            Assert.True(Total(limited) <= 100 + LogAttributeLimiter.MarkerAttributes);

            Assert.NotNull(overflow);
            var moved = Moved(overflow!.RenderMessage()).GetProperty("Big");
            Assert.Equal(Enumerable.Range(40, 110).Select(i => $"F{i}"), moved.EnumerateObject().Select(p => p.Name));
            Assert.Equal(149, moved.GetProperty("F149").GetInt32());
            Assert.Equal("Big", Scalar(overflow, LogAttributeLimiter.OverflowKeysProperty));
        }

        [Fact]
        public void Overflow_IsJoinedToItsParentAndKeepsTheParentContext()
        {
            var exception = new InvalidOperationException("boom");
            var traceId = ActivityTraceId.CreateRandom();
            var spanId = ActivitySpanId.CreateRandom();
            var logEvent = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Warning, exception,
                Parser.Parse("Onboarding initiated for AgencyId: {AgencyId}"),
                new[]
                {
                    new LogEventProperty("AgencyId", new ScalarValue(1055042)),
                    new LogEventProperty("Employee", Structure(300)),
                    new LogEventProperty("CorrelationId", new ScalarValue("c-1")),
                },
                traceId, spanId);

            var limited = LogAttributeLimiter.Limit(logEvent, 100, 4000, out var overflow);

            Assert.NotNull(overflow);
            Assert.True(Scalar(limited, LogAttributeLimiter.CollapsedProperty) as bool?);
            Assert.Equal(302, Scalar(limited, LogAttributeLimiter.OriginalCountProperty));
            Assert.Equal(Scalar(limited, LogAttributeLimiter.OverflowIdProperty), Scalar(overflow!, LogAttributeLimiter.OverflowIdProperty));
            Assert.Same(exception, limited.Exception);
            Assert.Null(overflow.Exception);
            Assert.Equal(traceId, limited.TraceId);
            Assert.Equal(spanId, limited.SpanId);
            Assert.Equal(traceId, overflow.TraceId);
            Assert.Equal(spanId, overflow.SpanId);
            Assert.Equal(LogEventLevel.Warning, overflow.Level);
            Assert.Equal(1055042, Scalar(overflow, "AgencyId"));
            Assert.Equal("c-1", Scalar(overflow, "CorrelationId"));
            Assert.False(overflow.Properties.ContainsKey("Employee"));
            Assert.False(overflow.Properties.ContainsKey(LogAttributeLimiter.CollapsedProperty));
            Assert.Equal("Onboarding initiated for AgencyId: 1055042", limited.RenderMessage());
        }

        [Fact]
        public void Overflow_KeepsJsonThatContainsBracesLiteral()
        {
            var logEvent = Event("Saved", Prop("Big", new StructureValue(Enumerable.Range(0, 20)
                .Select(i => new LogEventProperty($"F{i}", new ScalarValue("{Placeholder} and }{"))))));

            LogAttributeLimiter.Limit(logEvent, 5, 4000, out var overflow);

            var moved = Moved(overflow!.RenderMessage()).GetProperty("Big");
            Assert.Equal("{Placeholder} and }{", moved.GetProperty("F19").GetString());
        }

        [Fact]
        public void StructureWithNoMemberThatFits_BecomesTruncatedTextAndMovesWhole()
        {
            var big = new StructureValue(Enumerable.Range(0, 3).Select(i => new LogEventProperty($"Part{i}", Structure(100))));
            var logEvent = Event("Saved", Prop("Big", big));

            var limited = LogAttributeLimiter.Limit(logEvent, 50, 100, out var overflow);

            Assert.Equal(100, ((string)Scalar(limited, "Big")).Length);
            var moved = Moved(overflow!.RenderMessage()).GetProperty("Big");
            Assert.Equal(3, moved.EnumerateObject().Count());
            Assert.Equal(99, moved.GetProperty("Part2").GetProperty("F99").GetInt32());
        }

        [Fact]
        public void TooManyPlainProperties_MovesTheExcessButKeepsTemplateProperties()
        {
            var properties = Enumerable.Range(0, 40).Select(i => new LogEventProperty($"P{i}", new ScalarValue(i)))
                .Append(new LogEventProperty("Kept", new ScalarValue("value")))
                .ToArray();
            var logEvent = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, Parser.Parse("Value {Kept}"), properties);

            var limited = LogAttributeLimiter.Limit(logEvent, 10, 4000, out var overflow);

            Assert.True(Total(limited) <= 10 + LogAttributeLimiter.MarkerAttributes);
            Assert.Equal("Value \"value\"", limited.RenderMessage());
            var moved = Moved(overflow!.RenderMessage());
            Assert.Equal(31, moved.EnumerateObject().Count());
            Assert.Equal(0, moved.GetProperty("P0").GetInt32());
            Assert.False(limited.Properties.ContainsKey("P0"));
            Assert.False(moved.TryGetProperty("Kept", out _));
        }

        [Fact]
        public void TrimmedPropertyThatThenLeaves_IsInTheOverflowOnceAndWhole()
        {
            var properties = new[] { Prop("Big", Structure(10)) }
                .Concat(Enumerable.Range(0, 10).Select(i => Prop($"P{i}", new ScalarValue(i))))
                .ToArray();
            var logEvent = Event("Saved", properties);

            var limited = LogAttributeLimiter.Limit(logEvent, 5, 4000, out var overflow);

            Assert.False(limited.Properties.ContainsKey("Big"));
            var json = overflow!.RenderMessage();
            var moved = Moved(json).GetProperty("Big");
            Assert.Equal(10, moved.EnumerateObject().Count());
            Assert.Equal(1, json.Split(BigKey, StringSplitOptions.None).Length - 1);
        }

        [Theory]
        [InlineData(240, 0, 210)]
        [InlineData(240, 6, 204)]
        [InlineData(0, 6, 204)]
        [InlineData(-5, 0, 210)]
        [InlineData(5, 50, 1)]
        public void PropertyBudget_TakesSinkNewRelicMarkerAndResourceAttributesOff(int max, int resource, int expected)
        {
            Assert.Equal(expected, LogAttributeLimiter.PropertyBudget(max, resource));
        }

        [Fact]
        public void PlainOverflow_StaysWithinBudgetAndIsNotAJsonObjectMessage()
        {
            var logEvent = Event("Saved", Enumerable.Range(0, 300).Select(i => Prop($"P{i:D3}", new ScalarValue(i))).ToArray());

            var limited = LogAttributeLimiter.Limit(logEvent, 100, 4000, out var overflow);

            Assert.NotNull(overflow);
            Assert.True(Total(limited) <= 100 + LogAttributeLimiter.MarkerAttributes);
            Assert.True(Total(overflow!) <= 100 + LogAttributeLimiter.MarkerAttributes);
            Assert.NotEqual('{', overflow.RenderMessage().TrimStart()[0]);
            Assert.Equal(300, limited.Properties.Keys.Count(k => k[0] == 'P') + Moved(overflow.RenderMessage()).EnumerateObject().Count());
        }

        [Fact]
        public void OverTopLevelBudgetOnly_MovesScalarsAndKeepsStructuredValuesWhole()
        {
            var properties = new[] { Prop("Big", Structure(50)), Prop("Id", new ScalarValue(7)) }
                .Concat(Enumerable.Range(0, 150).Select(i => Prop($"P{i:D3}", new ScalarValue(i))))
                .ToArray();
            var logEvent = Event("Saved {Id}", properties);

            var limited = LogAttributeLimiter.Limit(logEvent, 1000, 4000, out var overflow);

            Assert.NotNull(overflow);
            Assert.Equal(LogAttributeLimiter.TopLevelBudget + LogAttributeLimiter.MarkerAttributes, limited.Properties.Count);
            Assert.True(overflow!.Properties.Count <= LogAttributeLimiter.TopLevelBudget + LogAttributeLimiter.MarkerAttributes);
            Assert.Equal(50, ((StructureValue)limited.Properties["Big"]).Properties.Count);
            Assert.Equal(7, Scalar(limited, "Id"));
            var moved = Moved(overflow.RenderMessage());
            Assert.Equal(152, limited.Properties.Count - LogAttributeLimiter.MarkerAttributes + moved.EnumerateObject().Count());
        }

        [Fact]
        public void EnrichedContext_IsNeverMoved()
        {
            var context = new[] { "level", "SourceContext", "CorrelationId", "trace.id", "span.id", "thread.id", "source.class", "source.line" };
            var properties = Enumerable.Range(0, 150).Select(i => Prop($"P{i:D3}", new ScalarValue(i)))
                .Concat(context.Select(k => Prop(k, new ScalarValue(k))))
                .ToArray();

            var limited = LogAttributeLimiter.Limit(Event("Saved", properties), 1000, 4000, out var overflow);

            Assert.NotNull(overflow);
            Assert.All(context, k => Assert.Equal(k, Scalar(limited, k)));
            Assert.All(context, k => Assert.Equal(k, Scalar(overflow!, k)));
        }

        [Fact]
        public void TopLevelBudget_LeavesRoomForSinkMarkerAndChunkAttributesUnderNewRelicsCap()
        {
            Assert.Equal(
                LogAttributeLimiter.MaxRecordAttributes,
                LogAttributeLimiter.TopLevelBudget + LogAttributeLimiter.SinkAttributes + LogAttributeLimiter.MarkerAttributes + ChunkingOpenTelemetrySink.ChunkAttributes);
        }

        [Theory]
        [InlineData("{\"a\":1,\"b\":{\"c\":2,\"d\":null},\"e\":[1,2]}", 3)]
        [InlineData("  {\"a\":1}", 1)]
        [InlineData("{not json}", 1)]
        [InlineData("[{\"a\":1},{\"b\":2}]", 1)]
        [InlineData("prefix {\"a\":1}", 1)]
        public void JsonStringValue_CountsAsTheFieldsNewRelicExpandsItInto(string text, int expected)
        {
            Assert.Equal(expected, LogAttributeLimiter.CountAttributes(new ScalarValue(text)));
        }

        [Fact]
        public void JsonStringProperty_OverBudget_MovesWholeToOverflow()
        {
            var logEvent = Event("Saved", Prop("Payload", new ScalarValue(JsonObject(150))), Prop("Id", new ScalarValue(7)));

            var limited = LogAttributeLimiter.Limit(logEvent, 100, 4000, out var overflow);

            Assert.False(limited.Properties.ContainsKey("Payload"));
            Assert.Equal(7, Scalar(limited, "Id"));
            Assert.Equal(JsonObject(150), Moved(overflow!.RenderMessage()).GetProperty("Payload").GetString());
        }

        [Fact]
        public void JsonMessage_WithinBudget_IsLeftForNewRelicToExpand()
        {
            var logEvent = Event("{Body:l}", Prop("Body", new ScalarValue(JsonObject(10))));

            Assert.Same(logEvent, LogAttributeLimiter.Limit(logEvent, 100, 4000, out var overflow));
            Assert.Null(overflow);
        }

        [Fact]
        public void JsonMessage_OverBudget_IsPrefixedSoItStaysText()
        {
            var logEvent = Event("{Body:l}", Prop("Body", new ScalarValue(JsonObject(150))));

            var limited = LogAttributeLimiter.Limit(logEvent, 100, 4000, out var overflow);

            Assert.Equal(LogAttributeLimiter.JsonMessagePrefix + JsonObject(150), limited.RenderMessage());
            Assert.True(Total(limited) <= 100 + LogAttributeLimiter.MarkerAttributes);
            // The message carries the whole value, so nothing is sent twice.
            Assert.Null(overflow);
        }

        [Fact]
        public void TemplateNamedStructure_OverBudget_KeepsTheWholeMessageAndTheFieldsThatFit()
        {
            var logEvent = Event("Saved {@Big}", Prop("Big", Structure(400)));

            var limited = LogAttributeLimiter.Limit(logEvent, 100, 4000, out var overflow);

            Assert.Contains("F399", limited.RenderMessage());
            Assert.Equal(100, ((StructureValue)limited.Properties["Big"]).Properties.Count);
            Assert.Null(overflow);
        }

        [Fact]
        public void LongArray_IsCutTo64AndTheWholeArrayMoves()
        {
            var logEvent = Event("Saved", Prop("Ids", Sequence(100)));

            var limited = LogAttributeLimiter.Limit(logEvent, 100, 4000, out var overflow);

            Assert.Equal(LogAttributeLimiter.MaxArrayLength, ((SequenceValue)limited.Properties["Ids"]).Elements.Count);
            Assert.Equal(100, Moved(overflow!.RenderMessage()).GetProperty("Ids").GetArrayLength());
        }

        [Fact]
        public void NestedLongArray_IsCutAndTheWholeValueMoves()
        {
            var employee = new StructureValue(new[] { new LogEventProperty("Name", new ScalarValue("x")), new LogEventProperty("Ids", Sequence(70)) });
            var logEvent = Event("Saved", Prop("Employee", employee));

            var limited = LogAttributeLimiter.Limit(logEvent, 100, 4000, out var overflow);

            var kept = (StructureValue)limited.Properties["Employee"];
            Assert.Equal(64, ((SequenceValue)kept.Properties.Single(p => p.Name == "Ids").Value).Elements.Count);
            Assert.Equal(70, Moved(overflow!.RenderMessage()).GetProperty("Employee").GetProperty("Ids").GetArrayLength());
        }

        [Fact]
        public void NullArrayEntries_AreDroppedWithoutAnOverflow()
        {
            var logEvent = Event("Saved", Prop("Ids", new SequenceValue(new[] { new ScalarValue(1), new ScalarValue(null), new ScalarValue(2) })));

            var limited = LogAttributeLimiter.Limit(logEvent, 100, 4000, out var overflow);

            Assert.Equal(new object[] { 1, 2 }, ((SequenceValue)limited.Properties["Ids"]).Elements.Select(e => ((ScalarValue)e).Value));
            Assert.Null(overflow);
        }

        [Fact]
        public void LongString_KeepsAPrefixAndMovesWhole()
        {
            var notes = new string('n', 5000);
            var logEvent = Event("Saved", Prop("Notes", new ScalarValue(notes)));

            var limited = LogAttributeLimiter.Limit(logEvent, 100, LogAttributeLimiter.TopLevelBudget, 100, 1000, null, out var overflow);

            Assert.Equal(100, ((string)Scalar(limited, "Notes")).Length);
            Assert.Equal(notes, Moved(overflow!.RenderMessage()).GetProperty("Notes").GetString());
        }

        [Fact]
        public void LongTemplateNamedString_FreezesTheMessageAndShortensInPlace()
        {
            var body = new string('b', 5000);
            var logEvent = Event("Body {Body:l}", Prop("Body", new ScalarValue(body)));

            var limited = LogAttributeLimiter.Limit(logEvent, 100, LogAttributeLimiter.TopLevelBudget, 100, 1000, null, out var overflow);

            Assert.Equal("Body " + body, limited.RenderMessage());
            Assert.Equal(100, ((string)Scalar(limited, "Body")).Length);
            Assert.Null(overflow);
        }

        [Fact]
        public void ShortStrings_AreLeftAlone()
        {
            var logEvent = Event("Saved {Body}", Prop("Body", new ScalarValue("short")));

            Assert.Same(logEvent, LogAttributeLimiter.Limit(logEvent, 100, LogAttributeLimiter.TopLevelBudget, 100, 1000, null, out _));
        }

        [Fact]
        public void DictionaryWithShortValues_IsLeftAlone()
        {
            var logEvent = Event("Saved", Prop("Map", Dictionary(3, i => new ScalarValue("v" + i))));

            Assert.Same(logEvent, LogAttributeLimiter.Limit(logEvent, 100, LogAttributeLimiter.TopLevelBudget, 100, 1000, null, out var overflow));
            Assert.Null(overflow);
        }

        [Fact]
        public void DictionaryWithALongString_KeepsAPrefixAndMovesWhole()
        {
            var body = new string('a', 2000);
            var logEvent = Event("Saved", Prop("Map", new DictionaryValue(new[]
            {
                Entry("short", new ScalarValue("x")),
                Entry("long", new ScalarValue(body)),
            })));

            var limited = LogAttributeLimiter.Limit(logEvent, 100, LogAttributeLimiter.TopLevelBudget, 100, 1000, null, out var overflow);

            var map = Elements((DictionaryValue)limited.Properties["Map"]);
            Assert.Equal("x", map["short"]);
            Assert.Equal(new string('a', 100), map["long"]);
            Assert.NotNull(overflow);
            Assert.Equal(body, Moved(overflow!.RenderMessage()).GetProperty("Map").GetProperty("long").GetString());
        }

        [Fact]
        public void OverBudgetDictionary_KeepsTheEntriesThatFitInOrderAndMovesTheRest()
        {
            var logEvent = Event("Saved", Prop("Map", Dictionary(50, i => new ScalarValue(i))));

            var limited = LogAttributeLimiter.Limit(logEvent, 20, 4000, out var overflow);

            var kept = Elements((DictionaryValue)limited.Properties["Map"]);
            Assert.Equal(Enumerable.Range(0, 20).Select(i => "k" + i), kept.Keys);
            Assert.NotNull(overflow);
            var moved = Moved(overflow!.RenderMessage()).GetProperty("Map").EnumerateObject().Select(p => p.Name);
            Assert.Equal(Enumerable.Range(20, 30).Select(i => "k" + i), moved);
        }

        [Fact]
        public void DictionaryWithNoEntryThatFits_BecomesTruncatedTextAndMovesWhole()
        {
            var logEvent = Event("Saved", Prop("Map", Dictionary(2, _ => Structure(30))));

            var limited = LogAttributeLimiter.Limit(logEvent, 20, 50, out var overflow);

            var text = Assert.IsType<string>(Scalar(limited, "Map"));
            Assert.Equal(50, text.Length);
            Assert.NotNull(overflow);
            var moved = Moved(overflow!.RenderMessage()).GetProperty("Map");
            Assert.Equal(new[] { "k0", "k1" }, moved.EnumerateObject().Select(p => p.Name));
            Assert.All(moved.EnumerateObject(), p => Assert.Equal(30, p.Value.EnumerateObject().Count()));
        }

        [Fact]
        public void Sink_LargeValueOnAChunkedEvent_IsSentOnceAndChunksShareAnId()
        {
            var options = new TestOptions { MaxLogMessageLength = 500, MaxAttributeValueLength = 400 };
            var captured = new CapturingSink();
            using var sink = ChunkingOpenTelemetrySink.WritingTo(options, new LoggerConfiguration().WriteTo.Sink(captured));
            var body = new string('b', 2000);

            sink.Emit(Event("Body {Body:l}", Prop("Body", new ScalarValue(body)), Prop("JobName", new ScalarValue("nightly"))));

            Assert.True(captured.Events.Count > 1);
            Assert.Equal("Body " + body, string.Concat(captured.Events.Select(e => e.RenderMessage())));
            Assert.Single(captured.Events.Select(e => Scalar(e, "chunk.id")).Distinct());
            Assert.All(captured.Events, e => Assert.Equal("nightly", Scalar(e, "JobName")));
            // The message carries the value; the property is cut on the first chunk and absent after it.
            Assert.Equal(400, ((string)Scalar(captured.Events[0], "Body")).Length);
            Assert.All(captured.Events.Skip(1), e => Assert.False(e.Properties.ContainsKey("Body")));
        }

        [Fact]
        public void Sink_WritesTheLimitedEventThenItsOverflow()
        {
            var options = new TestOptions { LogsMaxAttributes = 60 };
            var budget = LogAttributeLimiter.PropertyBudget(60, options.Attributes.Count);
            var captured = new CapturingSink();
            using var sink = ChunkingOpenTelemetrySink.WritingTo(options, new LoggerConfiguration().WriteTo.Sink(captured));

            sink.Emit(Event("Saved", Prop("Big", Structure(400))));

            Assert.Equal(2, captured.Events.Count);
            var (parent, overflow) = (captured.Events[0], captured.Events[1]);
            Assert.IsType<StructureValue>(parent.Properties["Big"]);
            Assert.True(Total(parent) <= budget + LogAttributeLimiter.MarkerAttributes);
            Assert.Equal(Scalar(parent, LogAttributeLimiter.OverflowIdProperty), Scalar(overflow, LogAttributeLimiter.OverflowIdProperty));
            Assert.Equal(400, CountInOverflow(parent, overflow, "Big"));
        }

        [Fact]
        public void Sink_ChunkedMessage_LeavesRoomForChunkAttributesAndWritesOneOverflow()
        {
            var options = new TestOptions { LogsMaxAttributes = 60, MaxLogMessageLength = 500 };
            var budget = LogAttributeLimiter.PropertyBudget(60, options.Attributes.Count);
            var captured = new CapturingSink();
            using var sink = ChunkingOpenTelemetrySink.WritingTo(options, new LoggerConfiguration().WriteTo.Sink(captured));

            sink.Emit(Event("Saved {A} {B}",
                Prop("A", new ScalarValue(new string('a', 300))),
                Prop("B", new ScalarValue(new string('b', 300))),
                Prop("Big", Structure(400))));

            var parents = captured.Events.Where(e => !e.Properties.ContainsKey(LogAttributeLimiter.OverflowKeysProperty)).ToList();
            var overflows = captured.Events.Except(parents).ToList();
            Assert.True(parents.Count > 1);
            Assert.True(overflows.Count > 1);
            Assert.All(captured.Events, e => Assert.True(Total(e) <= budget + LogAttributeLimiter.MarkerAttributes));
            Assert.Single(captured.Events.Select(e => Scalar(e, LogAttributeLimiter.OverflowIdProperty)).Distinct());
            Assert.Equal(Enumerable.Range(1, overflows.Count), overflows.Select(e => (int)Scalar(e, "chunk.index")));
            var json = string.Concat(overflows.Select(e => e.RenderMessage()));
            Assert.Equal(400, CountInOverflow(parents[0], Moved(json), "Big"));
        }

        private static JsonElement Moved(string message)
        {
            Assert.StartsWith(LogAttributeLimiter.OverflowMessagePrefix, message);
            return JsonDocument.Parse(message.Substring(LogAttributeLimiter.OverflowMessagePrefix.Length)).RootElement;
        }

        private static int CountInOverflow(LogEvent parent, LogEvent overflow, string key) =>
            CountInOverflow(parent, Moved(overflow.RenderMessage()), key);

        private static int CountInOverflow(LogEvent parent, JsonElement overflow, string key) =>
            ((StructureValue)parent.Properties[key]).Properties.Count + overflow.GetProperty(key).EnumerateObject().Count();

        private static LogEvent Event(string template, params LogEventProperty[] properties) =>
            new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, Parser.Parse(template), properties);

        private static LogEventProperty Prop(string name, LogEventPropertyValue value) => new LogEventProperty(name, value);

        private static SequenceValue Sequence(int count) =>
            new SequenceValue(Enumerable.Range(0, count).Select(i => new ScalarValue(i)));

        private static string JsonObject(int fields) =>
            "{" + string.Join(",", Enumerable.Range(0, fields).Select(i => $"\"j{i}\":{i}")) + "}";

        private static StructureValue Structure(int fields) =>
            new StructureValue(Enumerable.Range(0, fields).Select(i => new LogEventProperty($"F{i}", new ScalarValue(i))));

        private static KeyValuePair<ScalarValue, LogEventPropertyValue> Entry(string key, LogEventPropertyValue value) =>
            new KeyValuePair<ScalarValue, LogEventPropertyValue>(new ScalarValue(key), value);

        private static DictionaryValue Dictionary(int entries, Func<int, LogEventPropertyValue> value) =>
            new DictionaryValue(Enumerable.Range(0, entries).Select(i => Entry("k" + i, value(i))));

        private static Dictionary<string, object?> Elements(DictionaryValue dictionary) =>
            dictionary.Elements.ToDictionary(e => (string)e.Key.Value!, e => ((ScalarValue)e.Value).Value);

        private static object Scalar(LogEvent logEvent, string name) => ((ScalarValue)logEvent.Properties[name]).Value!;

        private static int Total(LogEvent logEvent) => logEvent.Properties.Values.Sum(LogAttributeLimiter.CountAttributes);

        private sealed class CapturingSink : ILogEventSink
        {
            public List<LogEvent> Events { get; } = new List<LogEvent>();

            public void Emit(LogEvent logEvent) => Events.Add(logEvent);
        }
    }
}
