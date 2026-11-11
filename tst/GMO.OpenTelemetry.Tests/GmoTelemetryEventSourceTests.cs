using System.Diagnostics.Tracing;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class GmoTelemetryEventSourceTests
    {
        private const string SourceName = "OpenTelemetry-Gmo-Shared";

        // Mono stubs EventSource: it ignores [EventSource(Name=...)] and never dispatches to listeners.
        // CI runs the net48 suite under Mono, while net48 in production is real .NET Framework, so these
        // assertions are skipped there rather than asserting behaviour that runtime does not implement.
        internal static readonly bool EventSourceDispatches = Type.GetType("Mono.Runtime") == null;

        internal sealed class Capture : EventListener
        {
            // Populated via field initializer: OnEventSourceCreated can fire from the base
            // constructor, before any constructor body would have run.
            private readonly List<EventWrittenEventArgs> _events = [];

            protected override void OnEventSourceCreated(EventSource eventSource)
            {
                if (eventSource.Name == SourceName)
                    EnableEvents(eventSource, EventLevel.Verbose);
            }

            protected override void OnEventWritten(EventWrittenEventArgs eventData)
            {
                lock (_events) _events.Add(eventData);
            }

            public List<EventWrittenEventArgs> ForComponent(string component)
            {
                lock (_events)
                {
                    return [.. _events.Where(e => e.Payload != null
                            && e.Payload.Count > 0
                            && (e.Payload[0] as string) == component)];
                }
            }

            public EventWrittenEventArgs Carrying(string value)
            {
                lock (_events)
                    return Assert.Single(_events, e => e.Payload != null && e.Payload.Contains(value));
            }
        }

        /// <summary>
        /// A malformed manifest leaves ConstructionException set and the source silently never emits —
        /// which would make the whole diagnostics channel a no-op, the exact failure it exists to catch.
        /// </summary>
        [Fact]
        public void EventSource_ConstructsWithoutError()
        {
            Assert.Null(GmoTelemetryEventSource.Log.ConstructionException);
            Assert.SkipUnless(EventSourceDispatches, "Mono ignores [EventSource(Name=...)]");
            Assert.Equal(SourceName, GmoTelemetryEventSource.Log.Name);
        }

        [Fact]
        public void OperationFailed_IsWrittenWithPayload()
        {
            var component = "comp-" + Guid.NewGuid().ToString("N");
            Assert.SkipUnless(EventSourceDispatches, "Mono EventSource does not dispatch to listeners");
            using var capture = new Capture();

            GmoTelemetryEventSource.Log.OperationFailed(component, "flushing batch", "InvalidOperationException: boom");

            var e = Assert.Single(capture.ForComponent(component));
            Assert.Equal(1, e.EventId);
            Assert.Equal(EventLevel.Error, e.Level);
            Assert.Equal("flushing batch", e.Payload![1]);
            Assert.Equal("InvalidOperationException: boom", e.Payload[2]);
        }

        [Fact]
        public void ItemsDropped_CarriesCountSoLossIsMeasurable()
        {
            var component = "comp-" + Guid.NewGuid().ToString("N");
            Assert.SkipUnless(EventSourceDispatches, "Mono EventSource does not dispatch to listeners");
            using var capture = new Capture();

            GmoTelemetryEventSource.Log.ItemsDropped(component, 42, "overflow channel full");

            var e = Assert.Single(capture.ForComponent(component));
            Assert.Equal(2, e.EventId);
            Assert.Equal(EventLevel.Warning, e.Level);
            Assert.Equal(42, e.Payload![1]);
            Assert.Equal("overflow channel full", e.Payload[2]);
        }

        [Fact]
        public void ExportRejected_CarriesStatusAndEndpoint()
        {
            var component = "comp-" + Guid.NewGuid().ToString("N");
            Assert.SkipUnless(EventSourceDispatches, "Mono EventSource does not dispatch to listeners");
            using var capture = new Capture();

            GmoTelemetryEventSource.Log.ExportRejected(component, 429, "https://log-api.newrelic.com/log/v1");

            var e = Assert.Single(capture.ForComponent(component));
            Assert.Equal(3, e.EventId);
            Assert.Equal(EventLevel.Error, e.Level);
            Assert.Equal(429, e.Payload![1]);
        }

        [Fact]
        public void Degraded_IsWrittenAtWarning()
        {
            var component = "comp-" + Guid.NewGuid().ToString("N");
            Assert.SkipUnless(EventSourceDispatches, "Mono EventSource does not dispatch to listeners");
            using var capture = new Capture();

            GmoTelemetryEventSource.Log.Degraded(component, "overflow delivery disabled");

            var e = Assert.Single(capture.ForComponent(component));
            Assert.Equal(4, e.EventId);
            Assert.Equal(EventLevel.Warning, e.Level);
            Assert.Equal("overflow delivery disabled", e.Payload![1]);
        }

        [Fact]
        public void NullArguments_DoNotThrow()
        {
            var exception = Record.Exception(() =>
            {
                GmoTelemetryEventSource.Log.OperationFailed(null!, null!, null!);
                GmoTelemetryEventSource.Log.ItemsDropped(null!, 0, null!);
                GmoTelemetryEventSource.Log.ExportRejected(null!, 0, null!);
                GmoTelemetryEventSource.Log.Degraded(null!, null!);
            });
            Assert.Null(exception);
        }

        [Fact]
        public void NullArguments_WhileListened_AreWrittenAsEmptyStrings()
        {
            var id = "id-" + Guid.NewGuid().ToString("N");
            Assert.SkipUnless(EventSourceDispatches, "Mono EventSource does not dispatch to listeners");
            using var capture = new Capture();

            GmoTelemetryEventSource.Log.OperationFailed(null!, id + "-op", null!);
            GmoTelemetryEventSource.Log.OperationFailed(id + "-failed", null!, "detail");
            GmoTelemetryEventSource.Log.ItemsDropped(null!, 1, id + "-dropped");
            GmoTelemetryEventSource.Log.ItemsDropped(id + "-dropping", 1, null!);
            GmoTelemetryEventSource.Log.ExportRejected(null!, 500, id + "-rejected");
            GmoTelemetryEventSource.Log.ExportRejected(id + "-rejecting", 500, null!);
            GmoTelemetryEventSource.Log.Degraded(null!, id + "-degraded");
            GmoTelemetryEventSource.Log.Degraded(id + "-degrading", null!);

            Assert.Equal(["", id + "-op", ""], capture.Carrying(id + "-op").Payload);
            Assert.Equal([id + "-failed", "", "detail"], capture.Carrying(id + "-failed").Payload);
            Assert.Equal(["", 1, id + "-dropped"], capture.Carrying(id + "-dropped").Payload);
            Assert.Equal([id + "-dropping", 1, ""], capture.Carrying(id + "-dropping").Payload);
            Assert.Equal(["", 500, id + "-rejected"], capture.Carrying(id + "-rejected").Payload);
            Assert.Equal([id + "-rejecting", 500, ""], capture.Carrying(id + "-rejecting").Payload);
            Assert.Equal(["", id + "-degraded"], capture.Carrying(id + "-degraded").Payload);
            Assert.Equal([id + "-degrading", ""], capture.Carrying(id + "-degrading").Payload);
        }

        [Fact]
        public void Describe_FormatsTypeAndMessage()
        {
            Assert.Equal("InvalidOperationException: boom",
                GmoTelemetryEventSource.Describe(new InvalidOperationException("boom")));
            Assert.Equal(string.Empty, GmoTelemetryEventSource.Describe(null!));
        }
    }
}
