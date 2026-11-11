using System.Diagnostics.Tracing;
using GMO.OpenTelemetry.Serilog;
using OpenTelemetry.Metrics;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    // The diagnostics state is process-wide, and other classes attach the Serilog writer as a side effect
    // of building a logger, so these run alone.
    [CollectionDefinition(nameof(TelemetryDiagnosticsState), DisableParallelization = true)]
    public class TelemetryDiagnosticsState { }

    [Collection(nameof(TelemetryDiagnosticsState))]
    public class TelemetryDiagnosticsTests : IDisposable
    {
        private static readonly bool EventSourceDispatches = Type.GetType("Mono.Runtime") == null;

        private readonly List<TelemetryProblem> _problems = new List<TelemetryProblem>();
        private readonly string _component = "test-" + Guid.NewGuid().ToString("N");
        private DateTime _now = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

        public TelemetryDiagnosticsTests()
        {
            TelemetryDiagnostics.ResetForTests();
            TelemetryDiagnostics.UtcNow = () => _now;
            TelemetryDiagnostics.WriteTo(p => { lock (_problems) _problems.Add(p); });
        }

        public void Dispose()
        {
            TelemetryDiagnostics.ResetForTests();
            SerilogTelemetryDiagnostics.Target = () => Log.Logger;
            global::Serilog.Debugging.SelfLog.Disable();
            GC.SuppressFinalize(this);
        }

        // Events from anything else that ran on the source while the test did are ignored.
        private List<TelemetryProblem> Mine()
        {
            lock (_problems)
                return _problems.Where(p => p.Detail.Contains(_component)).ToList();
        }

        [Fact]
        public void LibraryWarning_IsReportedAsAWarning()
        {
            Assert.SkipUnless(EventSourceDispatches, "Mono EventSource does not dispatch to listeners");

            GmoTelemetryEventSource.Log.ItemsDropped(_component, 5, "queue full");

            var problem = Assert.Single(Mine());
            Assert.False(problem.IsError);
            Assert.Equal(GmoTelemetryEventSource.SourceName, problem.Source);
            Assert.Contains("dropped 5 item(s) - queue full", problem.Detail);
        }

        [Fact]
        public void LibraryError_IsReportedAsAnError()
        {
            Assert.SkipUnless(EventSourceDispatches, "Mono EventSource does not dispatch to listeners");

            GmoTelemetryEventSource.Log.ExportRejected(_component, 413, "https://log-api.newrelic.com/log/v1");

            var problem = Assert.Single(Mine());
            Assert.True(problem.IsError);
            Assert.Contains("HTTP 413", problem.Detail);
        }

        // The metrics setup drops two runtime instruments on purpose; the SDK warns for each, and reporting
        // that would raise a false problem on every start. Other ignored instruments still count.
        [Fact]
        public void InstrumentDroppedByAView_IsNotReported_OtherIgnoredInstrumentsAre()
        {
            Assert.SkipUnless(EventSourceDispatches, "Mono EventSource does not dispatch to listeners");
            var meterName = "meter-" + _component;
            using var provider = global::OpenTelemetry.Sdk.CreateMeterProviderBuilder()
                .AddMeter(meterName)
                .AddView(i => i.Name == "dropped-" + _component ? MetricStreamConfiguration.Drop : null)
                .AddReader(new BaseExportingMetricReader(new NoOpMetricExporter()))
                .Build();
            using var meter = new System.Diagnostics.Metrics.Meter(meterName);

            meter.CreateCounter<long>("dropped-" + _component);
            meter.CreateCounter<long>("1invalid-" + _component);

            var problem = Assert.Single(Mine());
            Assert.Contains("1invalid-" + _component, problem.Detail);
        }

        private sealed class NoOpMetricExporter : global::OpenTelemetry.BaseExporter<Metric>
        {
            public override global::OpenTelemetry.ExportResult Export(in global::OpenTelemetry.Batch<Metric> batch) => global::OpenTelemetry.ExportResult.Success;
        }

        [Fact]
        public void Start_IsIdempotent()
        {
            Assert.SkipUnless(EventSourceDispatches, "Mono EventSource does not dispatch to listeners");

            TelemetryDiagnostics.Start();
            TelemetryDiagnostics.Start();

            GmoTelemetryEventSource.Log.ItemsDropped(_component, 1, "full");

            Assert.Single(Mine());
        }

        [Fact]
        public void RepeatsWithinTheInterval_AreSuppressedThenCounted()
        {
            for (var i = 0; i < 3; i++)
                TelemetryDiagnostics.Report("S", "E", "k", isError: false, _component);
            _now += TelemetryDiagnostics.ReportInterval;
            TelemetryDiagnostics.Report("S", "E", "k", isError: false, _component);

            var problems = Mine();
            Assert.Equal(2, problems.Count);
            Assert.Equal(2, problems[1].Suppressed);
        }

        [Fact]
        public void DifferentEvents_AreLimitedSeparately()
        {
            TelemetryDiagnostics.Report("S", "E1", "k", isError: false, _component);
            TelemetryDiagnostics.Report("S", "E2", "k", isError: true, _component);

            Assert.Equal(2, Mine().Count);
        }

        [Fact]
        public void ManyDistinctProblems_DoNotGrowTheRateLimitTableWithoutBound()
        {
            for (var i = 0; i < TelemetryDiagnostics.MaxWindows * 4; i++)
                TelemetryDiagnostics.Report("Serilog", "SelfLog", $"failure {i}", isError: false, _component);

            Assert.True(TelemetryDiagnostics.TrackedWindows <= TelemetryDiagnostics.MaxWindows + 1);
        }

        [Fact]
        public void AReportWrittenWhileReporting_DoesNotRecurse()
        {
            TelemetryDiagnostics.WriteTo(p =>
            {
                lock (_problems) _problems.Add(p);
                TelemetryDiagnostics.Report("S", "E2", "k2", isError: true, _component);
            });

            TelemetryDiagnostics.Report("S", "E1", "k1", isError: true, _component);

            Assert.Single(Mine());
        }

        [Fact]
        public void AWriterThatThrows_DoesNotEscape()
        {
            TelemetryDiagnostics.WriteTo(_ => throw new InvalidOperationException("writer failed"));

            var exception = Record.Exception(() => TelemetryDiagnostics.Report("S", "E", "k", isError: true, _component));

            Assert.Null(exception);
        }

        [Fact]
        public void Serilog_ReportsAreMarkedEventsOnTheTargetLogger()
        {
            var captured = new CapturingSink();
            SerilogTelemetryDiagnostics.Target = () => new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(captured).CreateLogger();
            SerilogTelemetryDiagnostics.Attach();

            TelemetryDiagnostics.Report("OpenTelemetry-Sdk", "ExportFailed", "1", isError: true, _component);

            var report = Assert.Single(captured.Events);
            Assert.Equal(LogEventLevel.Error, report.Level);
            Assert.True(Scalar(report, TelemetryDiagnostics.MarkerProperty) as bool?);
            Assert.Equal("OpenTelemetry-Sdk", Scalar(report, "TelemetrySource"));
            Assert.Equal(_component, Scalar(report, "TelemetryDetail"));
        }

        [Fact]
        public void Serilog_SelfLogIsRoutedHereOnAttach()
        {
            var captured = new CapturingSink();
            SerilogTelemetryDiagnostics.Target = () => new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(captured).CreateLogger();
            SerilogTelemetryDiagnostics.Attach();

            global::Serilog.Debugging.SelfLog.WriteLine("Exception while emitting periodic batch from {0}", _component);

            var report = Assert.Single(captured.Events);
            Assert.Equal("Serilog", Scalar(report, "TelemetrySource"));
            Assert.StartsWith("Exception while emitting periodic batch from " + _component, (string)Scalar(report, "TelemetryDetail")!);
        }

        [Fact]
        public void OpenTelemetrySink_SkipsMarkedEvents()
        {
            var inner = new CapturingSink();
            using var sink = ChunkingOpenTelemetrySink.WritingTo(new TestOptions(), new LoggerConfiguration().WriteTo.Sink(inner));
            var template = new MessageTemplateParser().Parse("export failing");

            sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Warning, null, template,
                new[] { new LogEventProperty(TelemetryDiagnostics.MarkerProperty, new ScalarValue(true)) }));
            sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Warning, null, template, Array.Empty<LogEventProperty>()));

            Assert.Single(inner.Events);
        }

        [Fact]
        public void ExpiredWindows_ArePrunedOnceTheTableIsFull()
        {
            for (var i = 0; i <= TelemetryDiagnostics.MaxWindows; i++)
                TelemetryDiagnostics.Report("S", "E", $"old {i}", isError: false, _component);
            _now += TelemetryDiagnostics.ReportInterval;

            TelemetryDiagnostics.Report("S", "E", "old 0", isError: false, _component);

            Assert.Equal(1, TelemetryDiagnostics.TrackedWindows);
            Assert.Equal(TelemetryDiagnostics.MaxWindows + 2, Mine().Count);
        }

        [Fact]
        public void AFullTableOfLiveWindows_IsClearedRatherThanGrown()
        {
            for (var i = 0; i <= TelemetryDiagnostics.MaxWindows; i++)
                TelemetryDiagnostics.Report("S", "E", $"live {i}", isError: false, _component);

            TelemetryDiagnostics.Report("S", "E", "live 0", isError: false, _component);

            Assert.Equal(1, TelemetryDiagnostics.TrackedWindows);
            Assert.Equal(TelemetryDiagnostics.MaxWindows + 2, Mine().Count);
        }

        [Fact]
        public void SelfLog_WithoutATimestamp_KeepsTheWholeLine()
        {
            TelemetryDiagnostics.ReportSelfLog("sink failed for " + _component);

            Assert.Equal("sink failed for " + _component, Assert.Single(Mine()).Detail);
        }

        [Fact]
        public void SelfLog_WithATimestamp_DropsIt()
        {
            TelemetryDiagnostics.ReportSelfLog("2026-10-22T12:00:00.0000000Z sink failed for " + _component);

            Assert.Equal("sink failed for " + _component, Assert.Single(Mine()).Detail);
        }

        [Fact]
        public void SelfLog_SingleWord_IsReportedWhole()
        {
            TelemetryDiagnostics.ReportSelfLog(_component);

            Assert.Equal(_component, Assert.Single(Mine()).Detail);
        }

        [Fact]
        public void SelfLog_Null_IsReportedAsEmpty()
        {
            TelemetryDiagnostics.ReportSelfLog(null!);

            lock (_problems)
                Assert.Equal(string.Empty, Assert.Single(_problems, p => p.Source == "Serilog").Detail);
        }

        [Fact]
        public void SelfLog_BeforeAWriterIsAttached_GoesOnlyToStandardError()
        {
            TelemetryDiagnostics.ResetForTests();
            var captured = CaptureStandardError(() => TelemetryDiagnostics.ReportSelfLog("sink failed for " + _component));

            Assert.Contains("sink failed for " + _component, captured);
            Assert.Equal(0, TelemetryDiagnostics.TrackedWindows);
        }

        [Fact]
        public void Report_BeforeAWriterIsAttached_GoesToStandardError()
        {
            TelemetryDiagnostics.ResetForTests();
            var captured = CaptureStandardError(() => TelemetryDiagnostics.Report("S", "E", "k", isError: true, _component));

            Assert.Contains("Telemetry export problem from S E: " + _component, captured);
        }

        [Fact]
        public void AStandardErrorThatThrows_DoesNotEscape()
        {
            TelemetryDiagnostics.ResetForTests();
            var error = Console.Error;
            Console.SetError(new ThrowingWriter());
            try
            {
                var exception = Record.Exception(() =>
                {
                    TelemetryDiagnostics.Report("S", "E", "k", isError: true, _component);
                    TelemetryDiagnostics.ReportSelfLog("sink failed for " + _component);
                });

                Assert.Null(exception);
            }
            finally
            {
                Console.SetError(error);
            }
        }

        private static string CaptureStandardError(Action action)
        {
            var error = Console.Error;
            using var captured = new StringWriter();
            Console.SetError(captured);
            try
            {
                action();
            }
            finally
            {
                Console.SetError(error);
            }
            return captured.ToString();
        }

        [Fact]
        public void Key_UsesTheFirstStringPayloadValue_CutTo120()
        {
            Assert.SkipUnless(EventSourceDispatches, "Mono EventSource does not dispatch to listeners");
            using var capture = new ProbeCapture();

            ProbeEventSource.Log.Named(new string('n', 200));

            var e = capture.Single(ProbeEventSource.NamedId);
            Assert.Equal(ProbeEventSource.NamedId + "|" + new string('n', 120), TelemetryDiagnostics.Key(e));
        }

        [Fact]
        public void Key_WithoutAStringFirstValue_IsTheEventId()
        {
            Assert.SkipUnless(EventSourceDispatches, "Mono EventSource does not dispatch to listeners");
            using var capture = new ProbeCapture();

            ProbeEventSource.Log.Empty();
            ProbeEventSource.Log.Number(7);

            Assert.Equal("1", TelemetryDiagnostics.Key(capture.Single(ProbeEventSource.EmptyId)));
            Assert.Equal("2", TelemetryDiagnostics.Key(capture.Single(ProbeEventSource.NumberId)));
        }

        [Fact]
        public void Describe_FormatsTheMessage()
        {
            Assert.SkipUnless(EventSourceDispatches, "Mono EventSource does not dispatch to listeners");
            using var capture = new ProbeCapture();

            ProbeEventSource.Log.Number(7);

            Assert.Equal("number 7", TelemetryDiagnostics.Describe(capture.Single(ProbeEventSource.NumberId)));
        }

        [Fact]
        public void Describe_WithABadMessageFormat_FallsBackToThePayload()
        {
            Assert.SkipUnless(EventSourceDispatches, "Mono EventSource does not dispatch to listeners");
            Assert.Null(ProbeEventSource.Log.ConstructionException);
            using var capture = new ProbeCapture();

            ProbeEventSource.Log.BadFormat("a", 2);

            Assert.Equal("a, 2", TelemetryDiagnostics.Describe(capture.Single(ProbeEventSource.BadFormatId)));
        }

        [Fact]
        public void Describe_WithoutAMessage_JoinsThePayload()
        {
            Assert.SkipUnless(EventSourceDispatches, "Mono EventSource does not dispatch to listeners");
            using var capture = new ProbeCapture();

            ProbeEventSource.Log.Empty();
            ProbeEventSource.Log.Named("x");

            Assert.Equal(string.Empty, TelemetryDiagnostics.Describe(capture.Single(ProbeEventSource.EmptyId)));
            Assert.Equal("x", TelemetryDiagnostics.Describe(capture.Single(ProbeEventSource.NamedId)));
        }

        [Fact]
        public void Listener_ReportsWarningsFromTheSourcesItIsGiven()
        {
            Assert.SkipUnless(EventSourceDispatches, "Mono EventSource does not dispatch to listeners");
            _ = ProbeEventSource.Log;
            using var listener = new TelemetryDiagnostics.DiagnosticsListener(new[] { ProbeEventSource.SourceName });

            ProbeEventSource.Log.Named(_component);

            var problem = Assert.Single(Mine());
            Assert.Equal(ProbeEventSource.SourceName, problem.Source);
            Assert.Equal(nameof(ProbeEventSource.Named), problem.EventName);
            Assert.False(problem.IsError);
        }

        // An existing source is parked while the base constructor runs; only the named ones are enabled.
        [Fact]
        public void Listener_DoesNotEnableAnExistingSourceItWasNotGiven()
        {
            Assert.SkipUnless(EventSourceDispatches, "Mono EventSource does not dispatch to listeners");
            _ = ProbeEventSource.Log;
            using var listener = new TelemetryDiagnostics.DiagnosticsListener(new[] { "GMO-Tests-Other-" + Guid.NewGuid().ToString("N") });

            ProbeEventSource.Log.Named(_component);

            Assert.Empty(Mine());
        }

        // The OTLP exporter's source is created lazily, after Start has run.
        [Fact]
        public void Listener_SubscribesToASourceCreatedAfterIt()
        {
            Assert.SkipUnless(EventSourceDispatches, "Mono EventSource does not dispatch to listeners");
            var name = "GMO-Tests-Late-" + Guid.NewGuid().ToString("N");
            using var listener = new TelemetryDiagnostics.DiagnosticsListener(new[] { name });
            using var source = new EventSource(name);

            source.Write("LateWarning", new EventSourceOptions { Level = EventLevel.Warning }, new { Component = _component });

            var problem = Assert.Single(Mine());
            Assert.Equal(name, problem.Source);
            Assert.Equal("LateWarning", problem.EventName);
        }

        // Self-describing events reach every listener once any of them enables the level, so the
        // listener has to filter below-warning events itself.
        [Fact]
        public void Listener_IgnoresEventsBelowWarning_EvenWhenAnotherListenerEnablesThem()
        {
            Assert.SkipUnless(EventSourceDispatches, "Mono EventSource does not dispatch to listeners");
            var name = "GMO-Tests-Verbose-" + Guid.NewGuid().ToString("N");
            using var source = new EventSource(name);
            using var listener = new TelemetryDiagnostics.DiagnosticsListener(new[] { name });
            using var verbose = new VerboseCapture(name);

            source.Write("Informational", new EventSourceOptions { Level = EventLevel.Informational }, new { Component = _component });
            source.Write("Error", new EventSourceOptions { Level = EventLevel.Error }, new { Component = _component });

            Assert.Equal(2, verbose.Count);
            var problem = Assert.Single(Mine());
            Assert.Equal("Error", problem.EventName);
            Assert.True(problem.IsError);
        }

        private sealed class VerboseCapture : EventListener
        {
            private readonly string _name;
            private int _count;

            public VerboseCapture(string name)
            {
                _name = name;
                foreach (var source in EventSource.GetSources().Where(s => s.Name == name))
                    EnableEvents(source, EventLevel.Verbose);
            }

            public int Count => Volatile.Read(ref _count);

            protected override void OnEventSourceCreated(EventSource eventSource)
            {
                if (_name != null && eventSource.Name == _name)
                    EnableEvents(eventSource, EventLevel.Verbose);
            }

            protected override void OnEventWritten(EventWrittenEventArgs eventData)
            {
                if (eventData.EventSource.Name == _name)
                    Interlocked.Increment(ref _count);
            }
        }

        private sealed class ThrowingWriter : TextWriter
        {
            public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

            public override void Write(char value) => throw new IOException("no console");

            public override void WriteLine(string? value) => throw new IOException("no console");
        }

        [EventSource(Name = SourceName)]
        private sealed class ProbeEventSource : EventSource
        {
            public const string SourceName = "GMO-Tests-TelemetryDiagnosticsProbe";
            public const int EmptyId = 1;
            public const int NumberId = 2;
            public const int BadFormatId = 3;
            public const int NamedId = 4;

            public static readonly ProbeEventSource Log = new ProbeEventSource();

            [Event(EmptyId, Level = EventLevel.Warning)]
            public void Empty() => WriteEvent(EmptyId);

            [Event(NumberId, Message = "number {0}", Level = EventLevel.Warning)]
            public void Number(int value) => WriteEvent(NumberId, value);

            [Event(BadFormatId, Message = "{0} {x}", Level = EventLevel.Warning)]
            public void BadFormat(string text, int value) => WriteEvent(BadFormatId, text, value);

            [Event(NamedId, Level = EventLevel.Warning)]
            public void Named(string name) => WriteEvent(NamedId, name);
        }

        private sealed class ProbeCapture : EventListener
        {
            private readonly List<EventWrittenEventArgs> _events = new List<EventWrittenEventArgs>();

            public EventWrittenEventArgs Single(int eventId)
            {
                lock (_events)
                    return Assert.Single(_events, e => e.EventId == eventId);
            }

            protected override void OnEventSourceCreated(EventSource eventSource)
            {
                if (eventSource.Name == ProbeEventSource.SourceName)
                    EnableEvents(eventSource, EventLevel.Verbose);
            }

            protected override void OnEventWritten(EventWrittenEventArgs eventData)
            {
                lock (_events) _events.Add(eventData);
            }
        }

        private static object? Scalar(LogEvent logEvent, string name) => ((ScalarValue)logEvent.Properties[name]).Value;

        private sealed class CapturingSink : ILogEventSink
        {
            public List<LogEvent> Events { get; } = new List<LogEvent>();

            public void Emit(LogEvent logEvent) => Events.Add(logEvent);
        }
    }
}
