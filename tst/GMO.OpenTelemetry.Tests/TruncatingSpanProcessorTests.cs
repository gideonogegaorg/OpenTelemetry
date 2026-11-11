using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using OpenTelemetry;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class TruncatingSpanProcessorTests : IDisposable
    {
        private readonly ActivitySource _source;
        private readonly ActivityListener _listener;

        public TruncatingSpanProcessorTests()
        {
            _source = new ActivitySource("Test.Truncating");
            _listener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose()
        {
            _listener.Dispose();
            _source.Dispose();
            GC.SuppressFinalize(this);
        }

        private static Mock<IOpenTelemetryOptions> BuildOptions(
            bool enabled = true,
            bool sqlEnabled = true,
            bool sqlText = true,
            int maxAttrLen = 10,
            int maxLogLen = 50,
            bool sendOverflow = true)
        {
            var opts = new Mock<IOpenTelemetryOptions>();
            opts.Setup(o => o.Enabled).Returns(enabled);
            opts.Setup(o => o.EnableSqlInstrumentation).Returns(sqlEnabled);
            opts.Setup(o => o.EnableSqlInstrumentationText).Returns(sqlText);
            opts.Setup(o => o.MaxAttributeValueLength).Returns(maxAttrLen);
            opts.Setup(o => o.MaxLogMessageLength).Returns(maxLogLen);
            opts.Setup(o => o.SendOverflowLogs).Returns(sendOverflow);
            opts.Setup(o => o.OverflowLogOperations).Returns(new HashSet<string>());
            opts.Setup(o => o.AdditionalKeysToLog).Returns(new HashSet<string>());
            opts.Setup(o => o.ServiceProvider).Returns(OpenTelemetryServiceProvider.NewRelic);
            opts.Setup(o => o.GetApiKey()).Returns("test-api-key");
            opts.Setup(o => o.OverflowChannelCapacity).Returns(100);
            opts.Setup(o => o.OverflowBatchSize).Returns(10);
            opts.Setup(o => o.OverflowBatchFlushIntervalMs).Returns(500);
            opts.Setup(o => o.OverflowMaxPayloadBytes).Returns(1_000_000);
            return opts;
        }

        private static Mock<IAttributeEnricher> BuildEnricher() =>
            new Mock<IAttributeEnricher>().Also(m =>
                // Fresh instance per call, as the real AttributeEnricher does. A shared dictionary would
                // alias every overflow chunk's attributes, hiding per-chunk regressions.
                m.Setup(e => e.Enrich(It.IsAny<Activity>()))
                    .Returns(() => new Dictionary<string, object>()));

        private static TruncatingSpanProcessor BuildProcessor(
            IOpenTelemetryOptions options, IAttributeEnricher enricher) =>
            new(options, enricher);

        [Fact]
        public void Constructor_Disabled_DoesNotThrow()
        {
            var opts = BuildOptions(enabled: false);
            var exception = Record.Exception(() =>
                new TruncatingSpanProcessor(opts.Object, BuildEnricher().Object));
            Assert.Null(exception);
        }

        [Fact]
        public void Constructor_NewRelic_DoesNotThrow()
        {
            var opts = BuildOptions();
            var exception = Record.Exception(() =>
                BuildProcessor(opts.Object, BuildEnricher().Object));
            Assert.Null(exception);
        }

        [Fact]
        public void Constructor_Dynatrace_ValidTenant_DoesNotThrow()
        {
            var opts = BuildOptions();
            opts.Setup(o => o.ServiceProvider).Returns(OpenTelemetryServiceProvider.Dynatrace);
            opts.Setup(o => o.GetOtlpEndpoint()).Returns(new Uri("https://mytenant.live.dynatrace.com"));
            opts.Setup(o => o.GetApiKey()).Returns("dt-api-token");
            var exception = Record.Exception(() =>
                BuildProcessor(opts.Object, BuildEnricher().Object));
            Assert.Null(exception);
        }

        // Attribute truncation is local work that must survive an unusable overflow target: a service
        // exporting through an unauthenticated local collector is valid, and telemetry configuration
        // must never stop the host booting. Only the direct-to-vendor overflow post is skipped.
        [Fact]
        public void Constructor_Dynatrace_InvalidTenant_DegradesInsteadOfThrowing()
        {
            var opts = BuildOptions();
            opts.Setup(o => o.ServiceProvider).Returns(OpenTelemetryServiceProvider.Dynatrace);
            opts.Setup(o => o.GetOtlpEndpoint()).Returns(new Uri("https://INVALID_TENANT.live.dynatrace.com"));
            opts.Setup(o => o.GetApiKey()).Returns("dt-api-token");

            var exception = Record.Exception(() => BuildProcessor(opts.Object, BuildEnricher().Object));

            Assert.Null(exception);
        }

        [Fact]
        public void Constructor_MissingApiKey_DegradesInsteadOfThrowing()
        {
            var opts = BuildOptions();
            opts.Setup(o => o.GetApiKey()).Returns((string?)null);

            var exception = Record.Exception(() => BuildProcessor(opts.Object, BuildEnricher().Object));

            Assert.Null(exception);
        }

        [Fact]
        public void Constructor_Dynatrace_MissingEndpoint_DegradesInsteadOfThrowing()
        {
            var opts = BuildOptions();
            opts.Setup(o => o.ServiceProvider).Returns(OpenTelemetryServiceProvider.Dynatrace);
            opts.Setup(o => o.GetOtlpEndpoint()).Returns((Uri?)null);
            opts.Setup(o => o.GetApiKey()).Returns("dt-api-token");

            var exception = Record.Exception(() => BuildProcessor(opts.Object, BuildEnricher().Object));

            Assert.Null(exception);
        }

        // Truncation must still happen when overflow delivery is unavailable.
        [Fact]
        public void OnEnd_WithoutApiKey_StillTruncatesAndMarks()
        {
            var opts = BuildOptions(maxAttrLen: 5, sqlText: true);
            opts.Setup(o => o.GetApiKey()).Returns((string?)null);
            var processor = BuildProcessor(opts.Object, BuildEnricher().Object);
            using var activity = _source.StartActivity("Test")!;
            activity.SetTag("db.statement", "0123456789");

            processor.OnEnd(activity);

            Assert.Equal("01234", activity.GetTagItem("db.statement"));
            Assert.True(activity.GetTagItem("db.statementTruncated") is true);
        }

        [Fact]
        public void OnEnd_NullActivity_DoesNotThrow()
        {
            var processor = BuildProcessor(BuildOptions().Object, BuildEnricher().Object);
            var exception = Record.Exception(() => processor.OnEnd(null!));
            Assert.Null(exception);
        }

        [Fact]
        public void OnEnd_SqlDisabled_StillTruncatesOversizedAttributes()
        {
            var opts = BuildOptions(sqlEnabled: false, maxAttrLen: 10);
            var processor = BuildProcessor(opts.Object, BuildEnricher().Object);
            using var activity = _source.StartActivity("Test")!;
            activity.SetTag("db.statement", new string('x', 100));
            processor.OnEnd(activity);
            // Truncation still applies — only keysToLog marking for db.* is gated on SQL
            Assert.Equal(new string('x', 10), activity.GetTagItem("db.statement"));
        }

        [Fact]
        public void OnEnd_ShortTag_NotTruncated()
        {
            var opts = BuildOptions(maxAttrLen: 100);
            var processor = BuildProcessor(opts.Object, BuildEnricher().Object);
            using var activity = _source.StartActivity("Test")!;
            activity.SetTag("db.statement", "short");
            processor.OnEnd(activity);
            Assert.Equal("short", activity.GetTagItem("db.statement"));
            Assert.Null(activity.GetTagItem("db.statementTruncated"));
        }

        [Fact]
        public void OnEnd_LongDbStatementTag_TruncatesAndMarks()
        {
            var opts = BuildOptions(maxAttrLen: 5, sqlText: true);
            var enricher = BuildEnricher();
            var processor = BuildProcessor(opts.Object, enricher.Object);
            using var activity = _source.StartActivity("Test")!;
            activity.SetTag("db.statement", "0123456789");
            processor.OnEnd(activity);
            Assert.Equal("01234", activity.GetTagItem("db.statement"));
            Assert.True(activity.GetTagItem("db.statementTruncated") is true);
        }

        [Fact]
        public void OnEnd_LongNonDbTag_TruncatesWithoutMarking()
        {
            var opts = BuildOptions(maxAttrLen: 5);
            var processor = BuildProcessor(opts.Object, BuildEnricher().Object);
            using var activity = _source.StartActivity("Test")!;
            activity.SetTag("custom.tag", "0123456789");
            processor.OnEnd(activity);
            Assert.Equal("01234", activity.GetTagItem("custom.tag"));
            Assert.Null(activity.GetTagItem("custom.tagTruncated"));
        }

        [Fact]
        public void OnEnd_WithAdditionalKeysToLog_TruncatesAndMarks()
        {
            var opts = BuildOptions(maxAttrLen: 5, sqlText: true);
            opts.Setup(o => o.AdditionalKeysToLog).Returns(new HashSet<string> { "custom.key" });
            var enricher = BuildEnricher();
            var processor = BuildProcessor(opts.Object, enricher.Object);
            using var activity = _source.StartActivity("Test")!;
            activity.SetTag("custom.key", "0123456789");
            processor.OnEnd(activity);
            Assert.Equal("01234", activity.GetTagItem("custom.key"));
            Assert.True(activity.GetTagItem("custom.keyTruncated") is true);
        }

        [Fact]
        public void OnEnd_TruncationBoundaryOnSurrogatePair_DoesNotSplitIt()
        {
            var opts = BuildOptions(maxAttrLen: 5, sqlText: false);
            var processor = BuildProcessor(opts.Object, BuildEnricher().Object);
            using var activity = _source.StartActivity("Test")!;
            // Boundary at index 5 lands between the high and low surrogate of the astral character.
            var astral = char.ConvertFromUtf32(0x1F600);
            activity.SetTag("custom.tag", "abcde" + astral + "fg");

            processor.OnEnd(activity);

            // This tag ships on the span itself, so a lone surrogate is replaced with U+FFFD by the
            // protobuf encoder or rejects the record outright.
            var value = (string)activity.GetTagItem("custom.tag")!;
            Assert.NotEmpty(value);
            Assert.False(char.IsHighSurrogate(value[value.Length - 1]),
                $"truncated tag '{value}' ends with an unpaired high surrogate");
        }

        [Fact]
        public void OnEnd_SendOverflowLogsFalse_EnqueuesNothingButStillTruncates()
        {
            var opts = BuildOptions(maxAttrLen: 5, sqlText: true, sendOverflow: false);
            var enricher = BuildEnricher();
            // Stub handler: a real one POSTs to the live NewRelic ingest endpoint from the test run.
            var processor = BuildProcessorWithHandler(opts.Object, enricher.Object, BuildOkHandler());
            using var activity = _source.StartActivity("Test")!;
            activity.SetTag("db.statement", "0123456789");

            processor.OnEnd(activity);
            processor.Dispose();

            // Disabling overflow logs must stop the raw statement leaving the process entirely,
            // while local truncation still applies.
            enricher.Verify(e => e.Enrich(It.IsAny<Activity>()), Times.Never);
            Assert.Equal("01234", activity.GetTagItem("db.statement"));
            Assert.True(activity.GetTagItem("db.statementTruncated") is true);
        }

        [Fact]
        public void OnEnd_NonPositiveMaxLogMessageLength_ClampsInsteadOfPostingUnbounded()
        {
            var opts = BuildOptions(maxAttrLen: 5, sqlText: true, maxLogLen: 0);
            var enricher = BuildEnricher();
            // Stub handler: without one this would gzip ~300 KB of test data to the live ingest endpoint.
            var processor = BuildProcessorWithHandler(opts.Object, enricher.Object, BuildOkHandler());
            using var activity = _source.StartActivity("Test")!;
            activity.SetTag("db.statement", new string('x', 300_000));

            processor.OnEnd(activity);
            processor.Dispose();

            // A non-positive limit means "no chunking" to the Serilog sink, but here it would post the
            // whole attribute as one unsplittable payload. Clamped to the default chunk length, one
            // Enrich per chunk.
            var chunks = (300_000 + OpenTelemetryOptionsBase.DefaultMaxLogMessageLength - 1) / OpenTelemetryOptionsBase.DefaultMaxLogMessageLength;
            enricher.Verify(e => e.Enrich(It.IsAny<Activity>()), Times.Exactly(chunks));
        }

        [Fact]
        public void OnEnd_OverflowLog_ChunkMetadataDescribesTheWholeValue()
        {
            var opts = BuildOptions(maxAttrLen: 5, sqlText: true, maxLogLen: 4_000);
            opts.Setup(o => o.OverflowBatchSize).Returns(50);
            opts.Setup(o => o.OverflowBatchFlushIntervalMs).Returns(50);
            var handler = BuildOkHandler();
            var processor = BuildProcessorWithHandler(opts.Object, BuildEnricher().Object, handler);
            using var activity = _source.StartActivity("Test")!;
            activity.SetTag("db.statement", new string('s', 19_761));

            processor.OnEnd(activity);
            processor.Dispose();

            var events = ReadPostedLogEvents(handler);
            Assert.Equal(5, events.Count);
            Assert.Equal(19_761, events.Sum(e => e.GetProperty("message").GetString()!.Length));
            Assert.All(events, e =>
            {
                var attrs = e.GetProperty("attributes");
                Assert.Equal(5, attrs.GetProperty("chunk.count").GetInt32());
                Assert.True(attrs.GetProperty("overflow").GetBoolean());
            });
            Assert.Equal(
                Enumerable.Range(1, 5),
                events.Select(e => e.GetProperty("attributes").GetProperty("chunk.index").GetInt32())
                    .OrderBy(i => i));
        }

        // A value the vendor accepts whole is not an overflow, however many records the batch carries.
        [Fact]
        public void OnEnd_OverflowLog_SingleChunk_ReportsOverflowFalse()
        {
            var opts = BuildOptions(maxAttrLen: 5, sqlText: true, maxLogLen: 4_000);
            opts.Setup(o => o.OverflowBatchSize).Returns(1);
            opts.Setup(o => o.OverflowBatchFlushIntervalMs).Returns(50);
            var handler = BuildOkHandler();
            var processor = BuildProcessorWithHandler(opts.Object, BuildEnricher().Object, handler);
            using var activity = _source.StartActivity("Test")!;
            activity.SetTag("db.statement", new string('s', 500));

            processor.OnEnd(activity);
            processor.Dispose();

            var attrs = Assert.Single(ReadPostedLogEvents(handler)).GetProperty("attributes");
            Assert.Equal(1, attrs.GetProperty("chunk.count").GetInt32());
            Assert.False(attrs.GetProperty("overflow").GetBoolean());
        }

        private static List<JsonElement> ReadPostedLogEvents(MockHttpHandler handler)
        {
            var events = new List<JsonElement>();
            foreach (var body in handler.Bodies)
            {
                using var source = new MemoryStream(body);
                using var gzip = new GZipStream(source, CompressionMode.Decompress);
                using var plain = new MemoryStream();
                gzip.CopyTo(plain);

                // Parsed documents are cloned: JsonDocument frees its buffer on dispose.
                using var doc = JsonDocument.Parse(plain.ToArray());
                events.AddRange(doc.RootElement.EnumerateArray().Select(e => e.Clone()));
            }

            return events;
        }

        [Fact]
        public void OnEnd_WhenDisabled_DoesNotThrow()
        {
            var opts = BuildOptions(enabled: false);
            var enricher = BuildEnricher();
            var processor = new TruncatingSpanProcessor(opts.Object, enricher.Object);
            using var activity = _source.StartActivity("Test")!;
            activity.SetTag("db.statement", new string('x', 100));
            var exception = Record.Exception(() => processor.OnEnd(activity));
            Assert.Null(exception);
        }

        [Fact]
        public void OnEnd_WhenDisabled_DoesNotCallEnricher()
        {
            var opts = BuildOptions(enabled: false);
            var enricher = BuildEnricher();
            var processor = new TruncatingSpanProcessor(opts.Object, enricher.Object);
            using var activity = _source.StartActivity("Test")!;
            activity.SetTag("db.statement", new string('x', 100));
            processor.OnEnd(activity);
            enricher.Verify(e => e.Enrich(It.IsAny<Activity>()), Times.Never);
        }

        [Fact]
        public void OnEnd_WhenEnabled_EnricherThrows_DoesNotPropagate()
        {
            var opts = BuildOptions(maxAttrLen: 5, sqlText: true);
            var enricher = new Mock<IAttributeEnricher>();
            enricher.Setup(e => e.Enrich(It.IsAny<Activity>()))
                .Throws(new InvalidOperationException("enricher failure"));
            var processor = BuildProcessor(opts.Object, enricher.Object);
            using var activity = _source.StartActivity("Test")!;
            activity.SetTag("db.statement", new string('x', 100));
            var exception = Record.Exception(() => processor.OnEnd(activity));
            Assert.Null(exception);
        }

        [Fact]
        public void OnEnd_ManyTags_OnlyOversizedTruncated()
        {
            var opts = BuildOptions(maxAttrLen: 10);
            var processor = BuildProcessor(opts.Object, BuildEnricher().Object);
            using var activity = _source.StartActivity("Test")!;

            for (int i = 0; i < 5; i++)
                activity.SetTag($"short.{i}", "ok");
            activity.SetTag("long.a", new string('a', 50));
            activity.SetTag("long.b", new string('b', 50));

            processor.OnEnd(activity);

            for (int i = 0; i < 5; i++)
                Assert.Equal("ok", activity.GetTagItem($"short.{i}"));

            Assert.Equal(new string('a', 10), activity.GetTagItem("long.a"));
            Assert.Equal(new string('b', 10), activity.GetTagItem("long.b"));
        }

        [Fact]
        public void OnEnd_NullTagValue_DoesNotThrow()
        {
            var opts = BuildOptions(maxAttrLen: 5);
            var processor = BuildProcessor(opts.Object, BuildEnricher().Object);
            using var activity = _source.StartActivity("Test")!;
            activity.SetTag("db.statement", (string?)null);
            var exception = Record.Exception(() => processor.OnEnd(activity));
            Assert.Null(exception);
        }

        [Fact]
        public void OnEnd_SqlTextDisabled_SkipsEmitButStillTruncates()
        {
            var opts = BuildOptions(maxAttrLen: 5, sqlText: false);
            var enricher = BuildEnricher();
            var processor = BuildProcessor(opts.Object, enricher.Object);
            using var activity = _source.StartActivity("Test")!;
            activity.SetTag("db.statement", "0123456789");
            processor.OnEnd(activity);
            // Should truncate but not call enricher (no emit)
            Assert.Equal("01234", activity.GetTagItem("db.statement"));
            Assert.Null(activity.GetTagItem("db.statementTruncated"));
            enricher.Verify(e => e.Enrich(It.IsAny<Activity>()), Times.Never);
        }

        [Fact]
        public void Constructor_NullOptions_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new TruncatingSpanProcessor(null!, BuildEnricher().Object));
        }

        [Fact]
        public void Constructor_NullEnricher_ThrowsArgumentNullException()
        {
            var opts = BuildOptions(enabled: false);
            Assert.Throws<ArgumentNullException>(() =>
                new TruncatingSpanProcessor(opts.Object, null!));
        }

        [Fact]
        public void Dispose_WhenDisabled_DoesNotThrow()
        {
            var opts = BuildOptions(enabled: false);
            var processor = new TruncatingSpanProcessor(opts.Object, BuildEnricher().Object);
            var exception = Record.Exception(() => processor.Dispose());
            Assert.Null(exception);
        }

        [Fact]
        public void Dispose_WhenEnabled_DoesNotThrow()
        {
            var opts = BuildOptions();
            var processor = BuildProcessor(opts.Object, BuildEnricher().Object);
            var exception = Record.Exception(() => processor.Dispose());
            Assert.Null(exception);
        }

        [Fact]
        public void OnEnd_SpanStartedMoreThan23HoursAgo_IsReported()
        {
            Assert.SkipUnless(GmoTelemetryEventSourceTests.EventSourceDispatches, "Mono EventSource does not dispatch to listeners");
            using var capture = new GmoTelemetryEventSourceTests.Capture();
            using var processor = BuildProcessorWithHandler(BuildOptions().Object, BuildEnricher().Object, BuildOkHandler());
            using var old = _source.StartActivity("HostLifetime")!;
            old.SetStartTime(DateTime.UtcNow.AddHours(-25));
            using var recent = _source.StartActivity("Recent")!;

            processor.OnEnd(old);
            processor.OnEnd(recent);

            var reports = capture.ForComponent(nameof(TruncatingSpanProcessor)).Where(e => (e.Payload![2] as string)?.Contains("24 h") == true).ToList();
            var report = Assert.Single(reports);
            Assert.Contains("'HostLifetime'", (string)report.Payload![2]!);
        }

        [Fact]
        public void OnEnd_LongStatusDescription_IsTruncated()
        {
            var opts = BuildOptions(maxAttrLen: 10);
            using var processor = BuildProcessorWithHandler(opts.Object, BuildEnricher().Object, BuildOkHandler());
            using var activity = _source.StartActivity("Test")!;
            activity.SetStatus(ActivityStatusCode.Error, new string('e', 100));

            processor.OnEnd(activity);

            Assert.Equal(ActivityStatusCode.Error, activity.Status);
            Assert.Equal(new string('e', 10), activity.StatusDescription);
        }

        [Fact]
        public void Dispose_Twice_ReportsNoFailure()
        {
            Assert.SkipUnless(GmoTelemetryEventSourceTests.EventSourceDispatches, "Mono EventSource does not dispatch to listeners");
            using var capture = new GmoTelemetryEventSourceTests.Capture();
            var processor = BuildProcessorWithHandler(BuildOptions().Object, BuildEnricher().Object, BuildOkHandler());

            processor.Dispose();
            processor.Dispose();

            Assert.DoesNotContain(capture.ForComponent(nameof(TruncatingSpanProcessor)), e => (e.Payload![1] as string) == "dispose");
        }

        private static TruncatingSpanProcessor BuildProcessorWithHandler(
            IOpenTelemetryOptions options, IAttributeEnricher enricher, HttpMessageHandler handler) =>
            new(options, enricher, handler);

        private static MockHttpHandler BuildOkHandler() => new(HttpStatusCode.OK);

        [Fact]
        public void OnEnd_OverflowLog_EnqueuedAndFlushed()
        {
            var opts = BuildOptions(maxAttrLen: 5, sqlText: true, maxLogLen: 20);
            opts.Setup(o => o.OverflowBatchSize).Returns(1);
            opts.Setup(o => o.OverflowBatchFlushIntervalMs).Returns(50);
            var enricher = BuildEnricher();
            // Dispose waits for the channel consumer to drain — deterministic, no Task.Delay
            var processor = BuildProcessorWithHandler(opts.Object, enricher.Object, BuildOkHandler());
            using var activity = _source.StartActivity("Test")!;
            activity.SetTag("db.statement", new string('x', 30));
            processor.OnEnd(activity);
            processor.Dispose();
            enricher.Verify(e => e.Enrich(It.IsAny<Activity>()), Times.AtLeastOnce);
        }

        [Fact]
        public void Dispose_DrainsPendingEntries()
        {
            var opts = BuildOptions(maxAttrLen: 5, sqlText: true, maxLogLen: 100);
            opts.Setup(o => o.OverflowBatchSize).Returns(50);
            opts.Setup(o => o.OverflowBatchFlushIntervalMs).Returns(5000); // very long — force drain on dispose
            var enricher = BuildEnricher();
            var handler = BuildOkHandler();
            var processor = BuildProcessorWithHandler(opts.Object, enricher.Object, handler);
            using var activity = _source.StartActivity("Test")!;
            activity.SetTag("db.statement", new string('x', 30));
            processor.OnEnd(activity);

            // Dispose before the flush interval elapses. The entry is either still in the channel or
            // already read into the pending batch, and the drain owes delivery either way.
            var exception = Record.Exception(() => processor.Dispose());

            Assert.Null(exception);
            Assert.Single(ReadPostedLogEvents(handler));
        }

        [Fact]
        public void OnEnd_OverflowLog_Dynatrace_UsesContentKey()
        {
            var opts = BuildOptions(maxAttrLen: 5, sqlText: true, maxLogLen: 20);
            opts.Setup(o => o.ServiceProvider).Returns(OpenTelemetryServiceProvider.Dynatrace);
            opts.Setup(o => o.GetOtlpEndpoint()).Returns(new Uri("https://mytenant.live.dynatrace.com"));
            opts.Setup(o => o.GetApiKey()).Returns("dt-token");
            opts.Setup(o => o.OverflowBatchSize).Returns(1);
            opts.Setup(o => o.OverflowBatchFlushIntervalMs).Returns(50);
            var enricher = BuildEnricher();
            var processor = BuildProcessorWithHandler(opts.Object, enricher.Object, BuildOkHandler());
            using var activity = _source.StartActivity("Test")!;
            activity.SetTag("db.statement", new string('d', 30));
            processor.OnEnd(activity);
            processor.Dispose();
            enricher.Verify(e => e.Enrich(It.IsAny<Activity>()), Times.AtLeastOnce);
        }

        private static Uri? PostedOverflowUri(string otlpEndpoint)
        {
            var opts = BuildOptions(maxAttrLen: 5, sqlText: true, maxLogLen: 20);
            opts.Setup(o => o.ServiceProvider).Returns(OpenTelemetryServiceProvider.Dynatrace);
            opts.Setup(o => o.GetOtlpEndpoint()).Returns(new Uri(otlpEndpoint));
            opts.Setup(o => o.GetApiKey()).Returns("dt-token");
            opts.Setup(o => o.OverflowBatchSize).Returns(1);
            opts.Setup(o => o.OverflowBatchFlushIntervalMs).Returns(50);

            var handler = BuildOkHandler();
            using var source = new ActivitySource("Test.Truncating");
            var processor = BuildProcessorWithHandler(opts.Object, BuildEnricher().Object, handler);
            using (var activity = source.StartActivity("Test")!)
            {
                activity.SetTag("db.statement", new string('d', 30));
                processor.OnEnd(activity);
            }
            processor.Dispose();
            return handler.LastRequest?.RequestUri;
        }

        // ActiveGate and Managed keep the tenant in the path, so rebuilding the URL from the first host
        // label aimed overflow logs at a live.dynatrace.com tenant that was not the caller's.
        [Theory]
        [InlineData("https://abc12345.live.dynatrace.com/api/v2/otlp",
                    "https://abc12345.live.dynatrace.com/api/v2/logs/ingest")]
        [InlineData("https://activegate.corp.local:9999/e/abc12345/api/v2/otlp",
                    "https://activegate.corp.local:9999/e/abc12345/api/v2/logs/ingest")]
        [InlineData("https://dt.example.com/e/abc12345/api/v2/otlp/v1/traces",
                    "https://dt.example.com/e/abc12345/api/v2/logs/ingest")]
        public void OverflowIngestUri_Dynatrace_DerivedFromConfiguredEndpoint(string otlp, string expected)
        {
            Assert.Equal(new Uri(expected), PostedOverflowUri(otlp));
        }

        // Endpoint without the OTLP API segment: fall back to the SaaS tenant shape as before.
        [Fact]
        public void OverflowIngestUri_Dynatrace_BareSaasHost_FallsBackToTenantShape()
        {
            Assert.Equal(new Uri("https://mytenant.live.dynatrace.com/api/v2/logs/ingest"),
                PostedOverflowUri("https://mytenant.live.dynatrace.com"));
        }

        private static MockHttpHandler RunOverflow(Mock<IOpenTelemetryOptions> opts, ActivitySource source, out Activity activity)
        {
            opts.Setup(o => o.OverflowBatchSize).Returns(1);
            opts.Setup(o => o.OverflowBatchFlushIntervalMs).Returns(50);
            var handler = BuildOkHandler();
            var processor = BuildProcessorWithHandler(opts.Object, BuildEnricher().Object, handler);
            activity = source.StartActivity("Test")!;
            activity.SetTag("db.statement", new string('c', 30));
            processor.OnEnd(activity);
            activity.Dispose();
            processor.Dispose();
            return handler;
        }

        // Configuration that omits ServiceProvider binds to value 0, which must be Collector: a generic
        // OTLP target has no vendor log API to receive the overflow post or the API key.
        [Fact]
        public void OverflowPost_ProviderNotConfigured_IsNotSent()
        {
            var opts = BuildOptions(maxAttrLen: 5, sqlText: true, maxLogLen: 20);
            opts.Setup(o => o.ServiceProvider).Returns(default(OpenTelemetryServiceProvider));

            var handler = RunOverflow(opts, _source, out var activity);

            Assert.Equal(0, handler.RequestCount);
            Assert.Equal("ccccc", activity.GetTagItem("db.statement"));
            Assert.True(activity.GetTagItem("db.statementTruncated") is true);
        }

        [Theory]
        [InlineData("http://localhost:4318")]
        [InlineData("https://otel-collector.internal:4317")]
        [InlineData("https://collector.example.com/e/abc12345/api/v2/otlp")]
        public void OverflowPost_Collector_IsNotSentEvenWithApiKeyAndEndpoint(string endpoint)
        {
            var opts = BuildOptions(maxAttrLen: 5, sqlText: true, maxLogLen: 20);
            opts.Setup(o => o.ServiceProvider).Returns(OpenTelemetryServiceProvider.Collector);
            opts.Setup(o => o.GetOtlpEndpoint()).Returns(new Uri(endpoint));
            opts.Setup(o => o.GetApiKey()).Returns("collector-key");

            var handler = RunOverflow(opts, _source, out var activity);

            Assert.Equal(0, handler.RequestCount);
            Assert.Null(handler.LastRequest);
            Assert.Equal("ccccc", activity.GetTagItem("db.statement"));
        }

        [Fact]
        public void OnEnd_MultiChunkOverflow_EnqueuesAllChunks()
        {
            var opts = BuildOptions(maxAttrLen: 5, sqlText: true, maxLogLen: 10);
            opts.Setup(o => o.OverflowBatchSize).Returns(10);
            opts.Setup(o => o.OverflowBatchFlushIntervalMs).Returns(50);
            var enricher = BuildEnricher();
            // 35 chars → 4 chunks of 10; dispose drains the channel deterministically
            var processor = BuildProcessorWithHandler(opts.Object, enricher.Object, BuildOkHandler());
            using var activity = _source.StartActivity("Test")!;
            activity.SetTag("db.statement", new string('m', 35));
            processor.OnEnd(activity);
            processor.Dispose();
            enricher.Verify(e => e.Enrich(It.IsAny<Activity>()), Times.AtLeast(4));
        }

        [Fact]
        public void OnEnd_OverflowLog_HttpFails_DoesNotPropagate()
        {
            var opts = BuildOptions(maxAttrLen: 5, sqlText: true, maxLogLen: 20);
            opts.Setup(o => o.OverflowBatchSize).Returns(1);
            opts.Setup(o => o.OverflowBatchFlushIntervalMs).Returns(50);
            var enricher = BuildEnricher();
            var handler = new MockHttpHandler(HttpStatusCode.InternalServerError, throwOnSend: true);
            using var processor = BuildProcessorWithHandler(opts.Object, enricher.Object, handler);
            using var activity = _source.StartActivity("Test")!;
            activity.SetTag("db.statement", new string('x', 30));
            var exception = Record.Exception(() => processor.OnEnd(activity));
            Assert.Null(exception);
        }

        [Fact]
        public void OnEnd_ChannelFull_DropsOldest_DoesNotThrow()
        {
            var opts = BuildOptions(maxAttrLen: 5, sqlText: true, maxLogLen: 10);
            opts.Setup(o => o.OverflowChannelCapacity).Returns(1);
            opts.Setup(o => o.OverflowBatchSize).Returns(100);
            opts.Setup(o => o.OverflowBatchFlushIntervalMs).Returns(60000); // very long so channel fills up
            var enricher = BuildEnricher();
            var processor = BuildProcessorWithHandler(opts.Object, enricher.Object, BuildOkHandler());

            // Enqueue many entries to overflow the capacity-1 channel
            for (int i = 0; i < 10; i++)
            {
                using var activity = _source.StartActivity($"Test{i}")!;
                activity.SetTag("db.statement", new string((char)('a' + i), 30));
                var exception = Record.Exception(() => processor.OnEnd(activity));
                Assert.Null(exception);
            }

            // Dispose should not throw even with dropped entries
            var disposeEx = Record.Exception(() => processor.Dispose());
            Assert.Null(disposeEx);
        }

        [Fact]
        public void OnEnd_AdditionalKeysToLog_WorksWithSqlDisabled()
        {
            var opts = BuildOptions(maxAttrLen: 5, sqlEnabled: false, sqlText: true);
            opts.Setup(o => o.AdditionalKeysToLog).Returns(new HashSet<string> { "custom.key" });
            var enricher = BuildEnricher();
            var processor = BuildProcessorWithHandler(opts.Object, enricher.Object, BuildOkHandler());
            using var activity = _source.StartActivity("Test")!;
            activity.SetTag("custom.key", "0123456789");
            processor.OnEnd(activity);
            // custom.key should be truncated and marked even with SQL disabled
            Assert.Equal("01234", activity.GetTagItem("custom.key"));
            Assert.True(activity.GetTagItem("custom.keyTruncated") is true);
            processor.Dispose();
        }

        [Fact]
        public void OnEnd_SqlDisabled_DbStatementNotInKeysToLog()
        {
            var opts = BuildOptions(maxAttrLen: 5, sqlEnabled: false, sqlText: true);
            var enricher = BuildEnricher();
            var processor = BuildProcessorWithHandler(opts.Object, enricher.Object, BuildOkHandler());
            using var activity = _source.StartActivity("Test")!;
            activity.SetTag("db.statement", "0123456789");
            processor.OnEnd(activity);
            // db.statement should still be truncated but NOT marked (not in keysToLog when SQL disabled)
            Assert.Equal("01234", activity.GetTagItem("db.statement"));
            Assert.Null(activity.GetTagItem("db.statementTruncated"));
            processor.Dispose();
        }

        // The payload cap is floored at 1024, so the batch has to genuinely exceed that to split. Left at
        // the 1,000,000 default this test never entered PostWithSplitAsync at all despite its name.
        [Fact]
        public void FlushBatch_LargePayload_SplitsIntoSubBatches()
        {
            var opts = BuildOptions(maxAttrLen: 5, sqlText: true, maxLogLen: 10);
            opts.Setup(o => o.OverflowBatchSize).Returns(100);
            opts.Setup(o => o.OverflowBatchFlushIntervalMs).Returns(5000);
            opts.Setup(o => o.OverflowMaxPayloadBytes).Returns(1024);
            var handler = BuildOkHandler();
            var processor = BuildProcessorWithHandler(opts.Object, BuildEnricher().Object, handler);

            // 5 x 100 chars at a 10-char chunk length = 50 entries, well past 1 KB serialized.
            for (int i = 0; i < 5; i++)
            {
                using var activity = _source.StartActivity($"BigTest{i}")!;
                activity.SetTag("db.statement", new string('z', 100));
                processor.OnEnd(activity);
            }

            var exception = Record.Exception(() => processor.Dispose());

            Assert.Null(exception);
            Assert.True(handler.RequestCount > 1,
                $"expected the batch to split across posts, saw {handler.RequestCount}");
            Assert.Equal(50, ReadPostedLogEvents(handler).Count);
        }

        // A non-positive cap turns truncation off entirely rather than clamping to zero-length tags.
        [Fact]
        public void Constructor_NonPositiveMaxAttributeValueLength_DisablesTruncation()
        {
            var opts = BuildOptions(maxAttrLen: 0, sqlText: true);
            var enricher = BuildEnricher();
            var processor = BuildProcessor(opts.Object, enricher.Object);
            using var activity = _source.StartActivity("Test")!;
            activity.SetTag("db.statement", new string('x', 100));

            processor.OnEnd(activity);

            Assert.Equal(new string('x', 100), activity.GetTagItem("db.statement"));
            Assert.Null(activity.GetTagItem("db.statementTruncated"));
            enricher.Verify(e => e.Enrich(It.IsAny<Activity>()), Times.Never);
        }

        // A batch already at capacity must flush without sitting out the interval first.
        [Fact]
        public void ConsumerLoop_BatchFillsImmediately_FlushesWithoutWaitingOutTheInterval()
        {
            var opts = BuildOptions(maxAttrLen: 5, sqlText: true, maxLogLen: 10);
            opts.Setup(o => o.OverflowBatchSize).Returns(2);
            opts.Setup(o => o.OverflowBatchFlushIntervalMs).Returns(30000);
            var handler = BuildOkHandler();
            var processor = BuildProcessorWithHandler(opts.Object, BuildEnricher().Object, handler);
            using var activity = _source.StartActivity("Test")!;
            activity.SetTag("db.statement", new string('m', 35));

            processor.OnEnd(activity);
            processor.Dispose();

            // 35 chars / 10 = 4 chunks. A 30s interval would have stalled them all past Dispose if the
            // full-batch short circuit were missing.
            Assert.Equal(4, ReadPostedLogEvents(handler).Count);
        }

        [Fact]
        public void PostPayload_SendsGzipContent()
        {
            var opts = BuildOptions(maxAttrLen: 5, sqlText: true, maxLogLen: 100);
            opts.Setup(o => o.OverflowBatchSize).Returns(1);
            opts.Setup(o => o.OverflowBatchFlushIntervalMs).Returns(50);
            var enricher = BuildEnricher();
            var handler = BuildOkHandler();
            var processor = BuildProcessorWithHandler(opts.Object, enricher.Object, handler);
            using var activity = _source.StartActivity("Test")!;
            activity.SetTag("db.statement", new string('g', 30));
            processor.OnEnd(activity);
            processor.Dispose();
            // Verify that at least one request was sent with gzip encoding
            Assert.True(handler.RequestCount > 0);
            Assert.NotNull(handler.LastRequest?.Content);
            Assert.Contains("gzip", handler.LastRequest!.Content!.Headers.ContentEncoding);
        }

        [Fact]
        public void FlushBatch_HttpThrows_SetsFailureCooldown_DoesNotThrow()
        {
            var opts = BuildOptions(maxAttrLen: 5, sqlText: true, maxLogLen: 100);
            opts.Setup(o => o.OverflowBatchSize).Returns(1);
            opts.Setup(o => o.OverflowBatchFlushIntervalMs).Returns(50);
            var enricher = BuildEnricher();
            // Use throwing handler to guarantee failure is recorded
            var handler = new MockHttpHandler(HttpStatusCode.OK, throwOnSend: true);
            using var processor = BuildProcessorWithHandler(opts.Object, enricher.Object, handler);

            // Enqueue entries - the exception in flush sets cooldown, suppressing subsequent attempts
            for (int i = 0; i < 3; i++)
            {
                using var activity = _source.StartActivity($"Cooldown{i}")!;
                activity.SetTag("db.statement", new string('c', 30));
                var exception = Record.Exception(() => processor.OnEnd(activity));
                Assert.Null(exception);
            }
        }

        [Fact]
        public void PostPayload_NonSuccessStatusCode_DoesNotEscapeToCaller()
        {
            var opts = BuildOptions(maxAttrLen: 5, sqlText: true, maxLogLen: 100);
            opts.Setup(o => o.OverflowBatchSize).Returns(1);
            opts.Setup(o => o.OverflowBatchFlushIntervalMs).Returns(50);
            var enricher = BuildEnricher();
            var handler = new MockHttpHandler(HttpStatusCode.BadRequest);
            var processor = BuildProcessorWithHandler(opts.Object, enricher.Object, handler);
            using var activity = _source.StartActivity("Test")!;
            activity.SetTag("db.statement", new string('b', 30));
            var exception = Record.Exception(() =>
            {
                processor.OnEnd(activity);
                processor.Dispose();
            });
            Assert.Null(exception);
        }
    }

    internal sealed class MockHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly bool _throwOnSend;
        private readonly List<byte[]> _bodies = new List<byte[]>();
        private int _requestCount;

        public int RequestCount => _requestCount;
        public HttpRequestMessage? LastRequest { get; private set; }

        /// <summary>Bodies are buffered here rather than read from <see cref="LastRequest"/>, whose
        /// content stream the processor disposes as soon as the post returns.</summary>
        public IReadOnlyList<byte[]> Bodies
        {
            get { lock (_bodies) return _bodies.ToArray(); }
        }

        public MockHttpHandler(HttpStatusCode statusCode, bool throwOnSend = false)
        {
            _statusCode = statusCode;
            _throwOnSend = throwOnSend;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            LastRequest = request;
            if (_throwOnSend)
                throw new HttpRequestException("Simulated HTTP failure");

            if (request.Content != null)
            {
#if NET
                var body = request.Content.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();
#else
                var body = request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
#endif
                lock (_bodies) _bodies.Add(body);
            }

            return Task.FromResult(new HttpResponseMessage(_statusCode));
        }
    }

    internal static class MockExtensions
    {
        internal static Mock<T> Also<T>(this Mock<T> mock, Action<Mock<T>> configure) where T : class
        {
            configure(mock);
            return mock;
        }
    }
}
