using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Linq;

namespace GMO.OpenTelemetry
{
    /// <summary>
    /// Makes telemetry export problems visible: the warnings and errors of the OpenTelemetry SDK, its OTLP
    /// exporter and this library's event source - failed and rejected exports, timeouts, dropped items -
    /// and, once a Serilog pipeline is attached, Serilog's own sink failures (<c>SelfLog</c>).
    /// </summary>
    /// <remarks>
    /// Without this they reach only the SDK's self-diagnostics file, which no environment enables, so lost
    /// telemetry looks like a quiet service. Each problem is written to the host's log through the writer
    /// its logging package attaches, marked with <see cref="MarkerProperty"/>; the OTLP log sink and
    /// appender skip marked events, because a report that export is failing must not depend on that export
    /// or feed back into it. Before a writer is attached, problems go to standard error. Reports are limited
    /// to one per source and event per minute, with the number suppressed in between.
    /// </remarks>
    public static class TelemetryDiagnostics
    {
        public const string MarkerProperty = "TelemetryDiagnostic";

        internal const string MessageTemplate =
            "Telemetry export problem from {TelemetrySource} {TelemetryEvent}: {TelemetryDetail} ({TelemetrySuppressed} similar suppressed)";

        internal static readonly string[] SourceNames =
        {
            "OpenTelemetry-Sdk",
            "OpenTelemetry-Exporter-OpenTelemetryProtocol",
            GmoTelemetryEventSource.SourceName,
        };

        internal static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(1);

        // SelfLog keys come from message text, which can vary without bound.
        internal const int MaxWindows = 512;

        private static readonly object StartLock = new object();
        private static readonly ConcurrentDictionary<string, Window> Windows = new ConcurrentDictionary<string, Window>();
        private static DiagnosticsListener? _listener;

        [ThreadStatic]
        private static bool _reporting;

        internal static Action<TelemetryProblem>? Writer { get; set; }
        internal static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        internal static int TrackedWindows => Windows.Count;

        /// <summary>Starts listening. Safe to call more than once.</summary>
        public static void Start()
        {
            lock (StartLock)
            {
                if (_listener == null)
                    _listener = new DiagnosticsListener(SourceNames);
            }
        }

        /// <summary>Routes reports to a logging framework; called by the Serilog and log4net packages.</summary>
        internal static void WriteTo(Action<TelemetryProblem> writer)
        {
            Writer = writer;
            Start();
        }

        /// <summary>Serilog <c>SelfLog</c> output: written to standard error and reported as a warning.</summary>
        internal static void ReportSelfLog(string message)
        {
            TryWriteStandardError(message);

            // SelfLog lines start with a timestamp; the rest identifies the failure.
            var body = message ?? string.Empty;
            var space = body.IndexOf(' ');
#if NET
            var stamp = body.AsSpan(0, Math.Max(0, space));
#else
            var stamp = space > 0 ? body.Substring(0, space) : string.Empty;
#endif
            if (space > 0 && DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
                body = body.Substring(space + 1);

            if (Writer != null)
                Report("Serilog", "SelfLog", body.Length > 120 ? body.Substring(0, 120) : body, isError: false, body);
        }

        internal static void Report(string source, string eventName, string key, bool isError, string detail)
        {
            if (_reporting)
                return;

            var now = UtcNow();
            if (Windows.Count > MaxWindows)
                PruneWindows(now);

            // AddOrUpdate returns the value this call stored, so the window itself says whether this
            // call opened it and how many reports the window it replaced held back.
            var window = Windows.AddOrUpdate($"{source}|{eventName}|{key}",
                _ => new Window(now, 0, opened: true, previouslySuppressed: 0),
                (_, w) => now - w.Started >= ReportInterval
                    ? new Window(now, 0, opened: true, previouslySuppressed: w.Suppressed)
                    : new Window(w.Started, w.Suppressed + 1, opened: false, previouslySuppressed: 0));

            if (!window.Opened)
                return;

            var problem = new TelemetryProblem(source, eventName, detail, isError, window.PreviouslySuppressed);
            try
            {
                _reporting = true;
                var writer = Writer;
                if (writer != null)
                    writer(problem);
                else
                    TryWriteStandardError(problem.ToString());
            }
            catch
            {
                // Reporting a telemetry problem must never become one.
            }
            finally
            {
                _reporting = false;
            }
        }

        internal static void ResetForTests()
        {
            lock (StartLock)
            {
                _listener?.Dispose();
                _listener = null;
            }
            Windows.Clear();
            Writer = null;
            UtcNow = () => DateTime.UtcNow;
        }

        // The SDK warns for every instrument a view drops, and the metrics setup drops two on purpose
        // (CustomMetrics.DropMisdeclaredRuntimeInstruments): reporting those would raise a false problem
        // on every start.
        internal static bool IsRequestedDrop(EventWrittenEventArgs e) =>
            e.EventName == "MetricInstrumentIgnored"
            && e.Payload != null
            && e.Payload.OfType<string>().Any(IsViewDropReason);

        // net48 has no string.Contains overload taking a comparison.
        private static bool IsViewDropReason(string text) =>
#if NET
            text.Contains(ViewDropReason, StringComparison.OrdinalIgnoreCase);
#else
            text.IndexOf(ViewDropReason, StringComparison.OrdinalIgnoreCase) >= 0;
#endif

        private const string ViewDropReason = "View configuration asks to drop";

        // The first payload value names the component or instrument, so one source's problem does not
        // hold back another's for the rest of the minute. Bounded: the window table is pruned.
        internal static string Key(EventWrittenEventArgs e)
        {
            var id = e.EventId.ToString(CultureInfo.InvariantCulture);
            if (e.Payload == null || e.Payload.Count == 0 || e.Payload[0] is not string first)
                return id;

            var name = first.Length > 120 ? first.Substring(0, 120) : first;
            return id + "|" + name;
        }

        internal static string Describe(EventWrittenEventArgs e)
        {
            var payload = e.Payload?.ToArray() ?? Array.Empty<object?>();
            if (!string.IsNullOrEmpty(e.Message))
            {
                try
                {
                    return string.Format(CultureInfo.InvariantCulture, e.Message, payload);
                }
                catch (FormatException)
                {
                    // Fall through to the raw payload.
                }
            }
            return string.Join(", ", payload.Select(p => p?.ToString()));
        }

        private static void PruneWindows(DateTime now)
        {
            foreach (var entry in Windows)
            {
                if (now - entry.Value.Started >= ReportInterval)
                    Windows.TryRemove(entry.Key, out _);
            }

            if (Windows.Count > MaxWindows)
                Windows.Clear();
        }

        private static void TryWriteStandardError(string? line)
        {
            try
            {
                Console.Error.WriteLine(line);
            }
            catch
            {
                // No console in some hosts (IIS, Cloud Services roles).
            }
        }

        private readonly struct Window
        {
            public Window(DateTime started, int suppressed, bool opened, int previouslySuppressed)
            {
                Started = started;
                Suppressed = suppressed;
                Opened = opened;
                PreviouslySuppressed = previouslySuppressed;
            }

            public DateTime Started { get; }
            public int Suppressed { get; }
            public bool Opened { get; }
            public int PreviouslySuppressed { get; }
        }

        internal sealed class DiagnosticsListener : EventListener
        {
            private readonly HashSet<string> _names;
            private readonly List<EventSource> _pending = new List<EventSource>();

            internal DiagnosticsListener(IEnumerable<string> names)
            {
                // The base constructor calls OnEventSourceCreated for every source that already exists,
                // before _names is assigned; those were parked and are enabled now. _names is assigned
                // under the same lock so a source created concurrently is either parked before this
                // drains the list or sees _names set and is enabled directly.
                lock (_pending)
                {
                    _names = new HashSet<string>(names, StringComparer.Ordinal);
                    foreach (var source in _pending.Where(s => _names.Contains(s.Name)))
                        EnableEvents(source, EventLevel.Warning, EventKeywords.All);
                    _pending.Clear();
                }
            }

            protected override void OnEventSourceCreated(EventSource eventSource)
            {
                // _names is null while the base constructor runs, despite the non-nullable declaration.
                // _pending is not: field initializers run before the base constructor.
                if (_names == null)
                {
                    lock (_pending)
                    {
                        if (_names == null)
                        {
                            _pending.Add(eventSource);
                            return;
                        }
                    }
                }

                if (_names.Contains(eventSource.Name))
                    EnableEvents(eventSource, EventLevel.Warning, EventKeywords.All);
            }

            protected override void OnEventWritten(EventWrittenEventArgs eventData)
            {
                if (eventData.Level > EventLevel.Warning || IsRequestedDrop(eventData))
                    return;

                Report(eventData.EventSource.Name,
                    eventData.EventName ?? eventData.EventId.ToString(CultureInfo.InvariantCulture),
                    Key(eventData),
                    isError: eventData.Level != EventLevel.Warning,
                    Describe(eventData));
            }
        }
    }

    /// <summary>One rate-limited telemetry problem, ready for a logging framework to write.</summary>
    internal sealed class TelemetryProblem
    {
        public TelemetryProblem(string source, string eventName, string detail, bool isError, int suppressed)
        {
            Source = source;
            EventName = eventName;
            Detail = detail;
            IsError = isError;
            Suppressed = suppressed;
        }

        public string Source { get; }
        public string EventName { get; }
        public string Detail { get; }
        public bool IsError { get; }
        public int Suppressed { get; }

        public override string ToString() =>
            $"Telemetry export problem from {Source} {EventName}: {Detail} ({Suppressed} similar suppressed)";
    }
}
