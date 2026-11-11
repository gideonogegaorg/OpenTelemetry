using GMO.OpenTelemetry.Serilog;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    // Serilog swallows exceptions from WriteTo sinks; AuditTo lets them reach the chunking sink's handlers.
    public class SerilogChunkingSinkFailureTests
    {
        private static readonly MessageTemplateParser Parser = new();
        private static readonly string[] EmitFailureMessages = ["Error in Emit", "Fallback write failed"];

        [Fact]
        public void Emit_WriteFails_ReportsTheErrorAndRetriesTheEvent()
        {
            var failures = 0;
            var inner = new SelectiveSink(e => IsSaved(e) && failures++ == 0);
            using var sink = ChunkingOpenTelemetrySink.WritingTo(new TestOptions(), new LoggerConfiguration().AuditTo.Sink(inner));

            sink.Emit(Saved());

            Assert.Equal(2, inner.Events.Count);
            Assert.Equal("Error in Emit", inner.Events[0].MessageTemplate.Text);
            AssertSinkFailure(inner.Events[0].Exception);
            Assert.True(IsSaved(inner.Events[1]));
            Assert.Equal(7, ((ScalarValue)inner.Events[1].Properties["Id"]).Value);
        }

        [Fact]
        public void Emit_FallbackWriteAlsoFails_ReportsBothFailuresWithoutThrowing()
        {
            var inner = new SelectiveSink(IsSaved);
            using var sink = ChunkingOpenTelemetrySink.WritingTo(new TestOptions(), new LoggerConfiguration().AuditTo.Sink(inner));

            sink.Emit(Saved());

            Assert.Equal(EmitFailureMessages, inner.Events.Select(e => e.MessageTemplate.Text));
            Assert.All(inner.Events, e => AssertSinkFailure(e.Exception));
        }

        [Fact]
        public void Emit_OneChunkFails_ReportsItAndStillWritesTheOtherChunks()
        {
            var inner = new SelectiveSink(e => ChunkIndex(e) == 2);
            var options = new TestOptions { MaxLogMessageLength = 500 };
            using var sink = ChunkingOpenTelemetrySink.WritingTo(options, new LoggerConfiguration().AuditTo.Sink(inner));

            sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null,
                Parser.Parse(new string('m', 1500)), []));

            var chunks = inner.Events.Where(e => ChunkIndex(e) != null).ToList();
            var count = (int)((ScalarValue)chunks[0].Properties["chunk.count"]).Value!;
            Assert.True(count > 2);
            Assert.Equal(Enumerable.Range(1, count).Where(i => i != 2), chunks.Select(e => ChunkIndex(e)!.Value));

            var error = Assert.Single(inner.Events, e => e.MessageTemplate.Text == "Failed to process chunk {ChunkIndex}");
            Assert.Equal(1, ((ScalarValue)error.Properties["ChunkIndex"]).Value);
            AssertSinkFailure(error.Exception);
        }

        [Fact]
        public void Dispose_InnerLoggerThrows_IsReportedAndDoesNotThrow()
        {
            var reason = "dispose-" + Guid.NewGuid().ToString("N");
            var inner = new SelectiveSink(_ => false) { DisposeFailure = new InvalidOperationException(reason) };
            var sink = ChunkingOpenTelemetrySink.WritingTo(new TestOptions(), new LoggerConfiguration().AuditTo.Sink(inner));
            using var capture = new GmoTelemetryEventSourceTests.Capture();

            sink.Dispose();

            Assert.True(inner.Disposed);
            Assert.SkipUnless(GmoTelemetryEventSourceTests.EventSourceDispatches, "Mono EventSource does not dispatch to listeners");
            Assert.Contains(capture.ForComponent(nameof(ChunkingOpenTelemetrySink)),
                e => (string?)e.Payload![1] == "disposing logger" && ((string?)e.Payload[2])!.Contains(reason));
        }

        // Serilog wraps an audit sink's exception, so the sink's own is the base exception.
        private static void AssertSinkFailure(Exception? exception) =>
            Assert.Equal(SelectiveSink.Failure, Assert.IsType<InvalidOperationException>(exception?.GetBaseException()).Message);

        private static LogEvent Saved() =>
            new(DateTimeOffset.UtcNow, LogEventLevel.Information, null, Parser.Parse("Saved {Id}"),
                [new LogEventProperty("Id", new ScalarValue(7))]);

        private static bool IsSaved(LogEvent logEvent) => logEvent.MessageTemplate.Text == "Saved {Id}";

        private static int? ChunkIndex(LogEvent logEvent) =>
            logEvent.Properties.TryGetValue("chunk.index", out var value) ? (int?)((ScalarValue)value).Value : null;

        private sealed class SelectiveSink(Func<LogEvent, bool> fails) : ILogEventSink, IDisposable
        {
            public const string Failure = "sink down";

            private readonly Func<LogEvent, bool> _fails = fails;

            public List<LogEvent> Events { get; } = [];

            public Exception? DisposeFailure { get; set; }

            public bool Disposed { get; private set; }

            public void Emit(LogEvent logEvent)
            {
                if (_fails(logEvent))
                    throw new InvalidOperationException(Failure);
                Events.Add(logEvent);
            }

            public void Dispose()
            {
                Disposed = true;
                if (DisposeFailure != null)
                    throw DisposeFailure;
            }
        }
    }
}
