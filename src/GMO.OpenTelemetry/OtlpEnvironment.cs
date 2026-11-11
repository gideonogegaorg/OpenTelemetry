using System;
using OpenTelemetry.Exporter;

namespace GMO.OpenTelemetry
{
    internal enum OtlpSignal
    {
        Traces,
        Metrics,
        Logs,
    }

    /// <summary>
    /// Endpoint, header and protocol resolution. Per-signal variables take precedence over the generic
    /// ones, per the OTLP exporter spec, so a deployment configured only through
    /// <c>OTEL_EXPORTER_OTLP_TRACES_ENDPOINT</c> resolves rather than looking unconfigured.
    /// </summary>
    internal static class OtlpEnvironment
    {
        private const string EndpointSuffix = "ENDPOINT";
        private const string HeadersSuffix = "HEADERS";

        internal static readonly Func<string, string?> SystemReader = Environment.GetEnvironmentVariable;

        internal static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

        /// <summary>Relative URIs (what a whitespace config value binds to) and non-HTTP schemes (what
        /// <c>localhost:4317</c> binds to) throw later in <c>UriBuilder</c> or <c>Uri.Port</c>.</summary>
        internal static Uri? SanitizeEndpoint(Uri? value)
            => value != null
               && value.IsAbsoluteUri
               && (value.Scheme == Uri.UriSchemeHttp || value.Scheme == Uri.UriSchemeHttps)
                ? value
                : null;

        internal static Uri? ResolveEndpoint(Uri? configured, OtlpSignal signal, Func<string, string?> read)
            => configured
               ?? ParseEndpoint(read(Variable(signal, EndpointSuffix)))
               ?? ParseEndpoint(read(GenericVariable(EndpointSuffix)));

        internal static string? ResolveHeaders(string? configured, OtlpSignal signal, Func<string, string?> read)
            => Blank(configured)
               ?? Blank(read(Variable(signal, HeadersSuffix)))
               ?? Blank(read(GenericVariable(HeadersSuffix)));

        /// <summary>For credential lookups with no signal context, such as <c>GetApiKey</c>.</summary>
        internal static string? ResolveHeaders(string? configured, Func<string, string?> read)
            => Blank(configured) ?? Blank(read(GenericVariable(HeadersSuffix)));

        // Modern TFMs only: net48 is HttpProtobuf-only, and the SDK marks OtlpExportProtocol.Grpc
        // obsolete there because its HttpClient cannot do HTTP/2.
#if !NETFRAMEWORK
        private const string ProtocolSuffix = "PROTOCOL";

        internal static OtlpExportProtocol ResolveProtocol(
            OtlpExportProtocol? configured, OtlpSignal signal, Func<string, string?> read)
            => configured
               ?? ParseProtocol(read(Variable(signal, ProtocolSuffix)))
               ?? ParseProtocol(read(GenericVariable(ProtocolSuffix)))
               ?? OtlpExportProtocol.Grpc;

        internal static OtlpExportProtocol? ParseProtocol(string? value)
        {
            var trimmed = Blank(value)?.Trim();
            if (trimmed == null)
                return null;

            // The spec spells these "grpc" and "http/protobuf"; the enum names differ.
            if (trimmed.Equals("http/protobuf", StringComparison.OrdinalIgnoreCase))
                return OtlpExportProtocol.HttpProtobuf;
            if (trimmed.Equals("grpc", StringComparison.OrdinalIgnoreCase))
                return OtlpExportProtocol.Grpc;

            // Enum.TryParse accepts numeric text, so "7" would yield an undefined value that only
            // fails much later, when the SDK builds the provider.
            return Enum.TryParse<OtlpExportProtocol>(trimmed, ignoreCase: true, out var parsed)
                   && Enum.IsDefined(typeof(OtlpExportProtocol), parsed)
                ? parsed
                : (OtlpExportProtocol?)null;
        }
#endif

        /// <param name="signal">"Traces", "Metrics" or "Logs".</param>
        /// <param name="optOut">Setting that disables this signal deliberately.</param>
        /// <param name="keys">Keys and variables that were checked, most specific first.</param>
        private const string NewRelicSuffix = "nr-data.net";
        private const string TraceObserverMarker = ".tracing.edge.nr-data.net";
        private const string DynatraceSuffix = "dynatrace.com";
        private const string DynatraceOtlpPath = "/api/v2/otlp";

        // otlp.nr-data.net plus the regional and FedRAMP variants; anchoring on the US host alone would
        // fire on every EU or JP tenant.
        private static readonly System.Text.RegularExpressions.Regex NewRelicOtlpHost =
            new(
                @"^(gov-)?otlp(\.[a-z0-9-]+)?\.nr-data\.net$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                    | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));

        /// <summary>Reports a logs or metrics endpoint that is demonstrably the wrong one: a vendor host
        /// that does not serve that signal. Deliberately silent on unrecognised hosts, since exporting
        /// through a collector is normal and warning on every one of those would be noise.</summary>
        internal static void WarnIfSignalEndpointMismatched(
            IOpenTelemetryOptions options, OtlpSignal signal, Uri? endpoint, string component)
        {
            var detail = DescribeSignalEndpointMismatch(options, signal, endpoint);
            if (detail != null)
                GmoTelemetryEventSource.Log.Degraded(component, detail);
        }

        internal static void WarnIfSignalEndpointMismatched(
            IOpenTelemetryOptions options, OtlpSignal signal, string? endpoint, string component)
            => WarnIfSignalEndpointMismatched(options, signal,
                Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed) ? parsed : null, component);

        /// <summary>The mismatch message, or null when the endpoint is usable or unrecognised.</summary>
        internal static string? DescribeSignalEndpointMismatch(
            IOpenTelemetryOptions options, OtlpSignal signal, Uri? endpoint)
        {
            // A trace observer host is the correct traces endpoint, so this check is logs/metrics only.
            if (endpoint == null || signal == OtlpSignal.Traces) return null;

            var setting = signal == OtlpSignal.Logs ? "Telemetry:LoggingEndpoint" : "Telemetry:MetricsEndpoint";
            string? detail = null;

            if (options.ServiceProvider == OpenTelemetryServiceProvider.NewRelic
                && endpoint.Host.EndsWith(NewRelicSuffix, StringComparison.OrdinalIgnoreCase)
                && !NewRelicOtlpHost.IsMatch(endpoint.Host))
            {
                detail = endpoint.Host.EndsWith(TraceObserverMarker, StringComparison.OrdinalIgnoreCase)
                    ? $"{signal} endpoint '{endpoint.Host}' is an Infinite Tracing trace observer, which accepts spans only - {signal} sent there is discarded. Set {setting} to a New Relic OTLP endpoint such as https://otlp.nr-data.net."
                    : $"{signal} endpoint '{endpoint.Host}' is not a New Relic OTLP endpoint; expected otlp[.region].nr-data.net or gov-otlp.nr-data.net. Set {setting}.";
            }
            else if (options.ServiceProvider == OpenTelemetryServiceProvider.Dynatrace
                && endpoint.Host.EndsWith(DynatraceSuffix, StringComparison.OrdinalIgnoreCase)
                && endpoint.AbsolutePath.IndexOf(DynatraceOtlpPath, StringComparison.OrdinalIgnoreCase) < 0)
            {
                detail = $"{signal} endpoint '{endpoint}' is a Dynatrace host but its path does not contain {DynatraceOtlpPath}; OTLP ingest rejects it. Set {setting}.";
            }

            return detail;
        }

        internal static InvalidOperationException MissingEndpoint(string signal, string optOut, params string[] keys)
            => new(
                $"OpenTelemetry is enabled and {signal} export is on, but no usable endpoint is configured. "
                + $"Set one of: {string.Join(", ", keys)}. "
                + "Endpoints must be absolute http/https URIs; blank or malformed values are ignored. "
                + $"To run without {signal} export, set {optOut}.");

        internal static string Variable(OtlpSignal signal, string suffix)
            => "OTEL_EXPORTER_OTLP_" + Token(signal) + "_" + suffix;

        internal static string GenericVariable(string suffix) => "OTEL_EXPORTER_OTLP_" + suffix;

        private static string Token(OtlpSignal signal) => signal switch
        {
            OtlpSignal.Metrics => "METRICS",
            OtlpSignal.Logs => "LOGS",
            _ => "TRACES",
        };

        private static Uri? ParseEndpoint(string? value)
            => Blank(value) != null && Uri.TryCreate(value, UriKind.Absolute, out var parsed)
                ? SanitizeEndpoint(parsed)
                : null;
    }
}
