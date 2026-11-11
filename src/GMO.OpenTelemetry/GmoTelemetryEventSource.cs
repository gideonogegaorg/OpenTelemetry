using System;
using System.Diagnostics.Tracing;

namespace GMO.OpenTelemetry
{
    /// <summary>
    /// One reporting channel for failures inside the GMO telemetry packages. The name must keep its
    /// "OpenTelemetry" prefix: the SDK's SelfDiagnosticsEventListener subscribes by that prefix, so these
    /// events land in an existing OTEL_DIAGNOSTICS.json with no extra wiring.
    /// </summary>
    [EventSource(Name = SourceName)]
    public sealed class GmoTelemetryEventSource : EventSource
    {
        public const string SourceName = "OpenTelemetry-Gmo-Shared";

        public static readonly GmoTelemetryEventSource Log = new();

        private GmoTelemetryEventSource() { }

        [Event(1, Message = "{0}: {1} failed: {2}", Level = EventLevel.Error)]
        public void OperationFailed(string component, string operation, string detail)
        {
            if (IsEnabled()) WriteEvent(1, component ?? "", operation ?? "", detail ?? "");
        }

        // ItemsDropped/ExportRejected box: EventSource has no fast (string, int, string) overload. Kept
        // deliberately - a queryable numeric count is worth more than one object[] on a failing path.
        [Event(2, Message = "{0}: dropped {1} item(s) - {2}", Level = EventLevel.Warning)]
        public void ItemsDropped(string component, int count, string reason)
        {
            if (IsEnabled()) WriteEvent(2, component ?? "", count, reason ?? "");
        }

        [Event(3, Message = "{0}: HTTP {1} from {2}", Level = EventLevel.Error)]
        public void ExportRejected(string component, int statusCode, string endpoint)
        {
            if (IsEnabled()) WriteEvent(3, component ?? "", statusCode, endpoint ?? "");
        }

        [Event(4, Message = "{0}: {1}", Level = EventLevel.Warning)]
        public void Degraded(string component, string detail)
        {
            if (IsEnabled()) WriteEvent(4, component ?? "", detail ?? "");
        }

        // Required by the EventSource contract: a non-event member without [NonEvent] can be picked up
        // by the manifest builder, and Exception is not a supported payload type - which would fail
        // construction and silence the whole channel.
        [NonEvent]
        public static string Describe(Exception ex)
        {
            if (ex == null) return string.Empty;
            return ex.GetType().Name + ": " + ex.Message;
        }
    }
}
