using System;
using System.Collections.Generic;
using System.Linq;
using OpenTelemetry.Exporter;

namespace GMO.OpenTelemetry
{
    public interface IOpenTelemetryOptions
    {
        bool Enabled { get; set; }
        string EnvironmentName { get; set; }
        string InstanceName { get; set; }
        string[] TraceSourceNames { get; set; }
        string[] MeterSourceNames { get; set; }
        bool EnableSqlInstrumentation { get; set; }
        bool EnableSqlInstrumentationText { get; set; }
        bool EnableHttpInstrumentation { get; set; }
        bool EnableHttpRecordException { get; set; }
        bool EnableLogging { get; set; }
        OpenTelemetryServiceProvider ServiceProvider { get; set; }
        int MaxAttributeValueLength { get; set; }
        int MaxLogMessageLength { get; set; }
        bool SendOverflowLogs { get; set; }
        HashSet<string> OverflowLogOperations { get; set; }
        HashSet<string> AdditionalKeysToLog { get; set; }
        string ServiceName { get; }
        string Version { get; }
        Dictionary<string, object> Attributes { get; }
        bool EnableConsole { get; set; }
        bool EnableGzipCompression { get; set; }

        bool IncludeSourceLocation { get; set; }
        bool IncludeThreadInfo { get; set; }
        bool IncludeUserInfo { get; set; }

        string[] LogStackExclusions { get; set; }

        int TracesMaxQueueSize { get; set; }
        int TracesExportDelayMs { get; set; }
        int TracesExportTimeoutMs { get; set; }
        int TracesMaxBatchSize { get; set; }

        int MetricsMaxQueueSize { get; set; }
        int MetricsExportDelayMs { get; set; }
        int MetricsExportTimeoutMs { get; set; }
        int MetricsMaxBatchSize { get; set; }

        int LogsMaxQueueSize { get; set; }
        int LogsExportDelayMs { get; set; }
        int LogsExportTimeoutMs { get; set; }
        int LogsMaxBatchSize { get; set; }

        int OverflowChannelCapacity { get; set; }
        int OverflowBatchSize { get; set; }
        int OverflowBatchFlushIntervalMs { get; set; }
        int OverflowMaxPayloadBytes { get; set; }

        string? LoggingEndpoint { get; set; }
        Uri? MetricsEndpoint { get; set; }

        Uri? GetOtlpEndpoint();
        string? GetApiKey();

        /// <summary>
        /// Raw OTLP exporter headers string from configuration
        /// (e.g. <c>api-key=...</c> or <c>Authorization=Api-Token ...</c>).
        /// Prefer this when configuring exporters; <see cref="GetApiKey"/> strips the header name.
        /// </summary>
        string? GetOtlpHeaders();

#if !NETFRAMEWORK
        /// <summary>Deliberately not <see cref="OtlpExporterOptions"/>: the binder writes into this
        /// reflectively, and that SDK type rejects the null an empty config value binds to.</summary>
        OtlpSettings Otlp { get; set; }
#endif
    }

    public abstract class OpenTelemetryOptionsBase : IOpenTelemetryOptions
    {
        protected const int DefaultMaxAttributeValueLength = 4000;
        // Sized to New Relic's 1 MB export request, which it rejects whole - every record in the batch,
        // not just the large one. A chunk is sent twice (body and message_template.text), so a full
        // batch of DefaultLogsMaxBatchSize chunks is about 2 MB before compression and well under 1 MB
        // after it for text. New Relic keeps up to 4,094 characters in a field and 128,000 UTF-8 bytes
        // more in a newrelic.ext. blob, so never raise it past 130,000; raising it at all trades the
        // request limit for fewer chunks. internal as well as protected: TruncatingSpanProcessor clamps
        // against the same cap, and a second copy of the constant would drift.
        protected internal const int DefaultMaxLogMessageLength = 16000;

        // Records per log export request; see DefaultMaxLogMessageLength for how the two bound its size.
        protected internal const int DefaultLogsMaxBatchSize = 64;

        private static readonly char[] HeaderSeparators = new[] { ';', ',' };
        private static readonly char[] KvSeparator = new[] { '=' };

        public bool Enabled { get; set; } = false;
        public string EnvironmentName { get; set; } = "undefined";
        public string InstanceName { get; set; } = Environment.MachineName;
        public string[] TraceSourceNames { get; set; } = new[] { "*" };
        public string[] MeterSourceNames { get; set; } = new[] { "*" };
        public bool EnableSqlInstrumentation { get; set; } = true;
        public bool EnableSqlInstrumentationText { get; set; } = true;
        public bool EnableHttpInstrumentation { get; set; } = true;
        public bool EnableHttpRecordException { get; set; } = true;
        public bool EnableLogging { get; set; } = true;
        public OpenTelemetryServiceProvider ServiceProvider { get; set; }
        public int MaxAttributeValueLength { get; set; } = DefaultMaxAttributeValueLength;
        public int MaxLogMessageLength { get; set; } = DefaultMaxLogMessageLength;
        public bool SendOverflowLogs { get; set; } = true;
        public HashSet<string> OverflowLogOperations { get; set; } = new HashSet<string>();
        public HashSet<string> AdditionalKeysToLog { get; set; } = new HashSet<string>();
        public bool EnableConsole { get; set; } = false;
        public bool EnableGzipCompression { get; set; } = true;

        public bool IncludeSourceLocation { get; set; } = true;
        public bool IncludeThreadInfo { get; set; } = true;
        public bool IncludeUserInfo { get; set; } = true;

        public string[] LogStackExclusions { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Object levels written when a value is serialized: span tags and event attributes, and Serilog
        /// <c>{@...}</c> destructuring when the OTLP log pipeline is attached. Each level runs the object's
        /// getters - an EF lazy-loading entity queries the database - and becomes log fields, so keep it
        /// low. Zero or less means the default.
        /// </summary>
        public int MaxSerializationDepth { get; set; } = ObjectRenderer.DefaultMaxDepth;

        internal const int DefaultLogsMaxAttributes = 240;

        /// <summary>
        /// Attributes one OTLP log record may carry, counted as New Relic stores it. New Relic drops a whole
        /// record over 255, reported only as an <c>NrIntegrationError</c>; the default leaves margin for
        /// attributes nothing here can see. Over it, the Serilog sink trims the largest structured
        /// properties and sends the rest in a linked overflow record. Zero or less means the default.
        /// </summary>
        public int LogsMaxAttributes { get; set; } = DefaultLogsMaxAttributes;

        public int TracesMaxQueueSize { get; set; } = 2048;
        public int TracesExportDelayMs { get; set; } = 5000;
        public int TracesExportTimeoutMs { get; set; } = 30000;
        public int TracesMaxBatchSize { get; set; } = 512;

        /// <summary>No effect: metrics are collected on an interval, not queued.</summary>
        public int MetricsMaxQueueSize { get; set; } = 4096;

        /// <summary>Metric collection interval. Matches the SDK default — lowering it multiplies
        /// ingest volume rather than improving resolution of anything but gauges.</summary>
        public int MetricsExportDelayMs { get; set; } = 60000;

        public int MetricsExportTimeoutMs { get; set; } = 30000;

        /// <summary>No effect: metrics are not batched this way.</summary>
        public int MetricsMaxBatchSize { get; set; } = 1024;

        public int LogsMaxQueueSize { get; set; } = 2048;
        public int LogsExportDelayMs { get; set; } = 5000;
        public int LogsExportTimeoutMs { get; set; } = 30000;
        public int LogsMaxBatchSize { get; set; } = DefaultLogsMaxBatchSize;

        /// <summary>
        /// Maximum number of overflow log entries queued for batch HTTP delivery.
        /// When full, new entries are dropped (truncated data remains on the span).
        /// </summary>
        public int OverflowChannelCapacity { get; set; } = 1000;

        /// <summary>
        /// Maximum entries per HTTP batch sent to the APM provider for overflow logs.
        /// </summary>
        public int OverflowBatchSize { get; set; } = 50;

        /// <summary>
        /// Maximum time in milliseconds to wait before flushing a partial overflow batch.
        /// </summary>
        public int OverflowBatchFlushIntervalMs { get; set; } = 2000;

        /// <summary>
        /// Maximum serialized JSON payload size in bytes for a single overflow HTTP request.
        /// Batches exceeding this size are recursively split until each sub-batch fits.
        /// </summary>
        public int OverflowMaxPayloadBytes { get; set; } = 1_000_000;

        private string? _loggingEndpoint;
        private Uri? _metricsEndpoint;
        private string? _rejectedLoggingEndpoint;
        private string? _rejectedMetricsEndpoint;

        /// <summary>Blank normalises to null so it falls through to the OTLP endpoint instead of
        /// shadowing it and silently disabling log export.</summary>
        public string? LoggingEndpoint
        {
            get => _loggingEndpoint;
            set
            {
                var blank = OtlpEnvironment.Blank(value);
                var usable = blank != null
                             && Uri.TryCreate(blank, UriKind.Absolute, out var parsed)
                             && OtlpEnvironment.SanitizeEndpoint(parsed) != null;

                _loggingEndpoint = usable ? blank : null;
                _rejectedLoggingEndpoint = usable ? null : blank;
            }
        }

        public Uri? MetricsEndpoint
        {
            get => _metricsEndpoint;
            set
            {
                _metricsEndpoint = OtlpEnvironment.SanitizeEndpoint(value);
                // A whitespace config value binds to a relative Uri, which is still the blank opt-out.
                _rejectedMetricsEndpoint = _metricsEndpoint == null
                    ? OtlpEnvironment.Blank(value?.OriginalString)
                    : null;
            }
        }

        /// <summary>
        /// Reports values that were set but unusable. Separate from the missing-endpoint guards because
        /// a malformed per-signal override resolves to a non-null fallback, so it would otherwise be
        /// silently ignored - the exact failure this reports.
        /// </summary>
        public virtual void ThrowIfEndpointsMalformed()
        {
            var candidates = new[]
            {
                ("Telemetry:MetricsEndpoint", _rejectedMetricsEndpoint),
                ("Telemetry:LoggingEndpoint", _rejectedLoggingEndpoint),
#if !NETFRAMEWORK
                ("Telemetry:Otlp:Endpoint", Otlp?.RejectedEndpoint),
#endif
            };

            var rejected = candidates
                .Where(c => c.Item2 != null)
                .Select(c => $"'{c.Item1}' is set to '{c.Item2}'")
                .ToList();

            if (rejected.Count > 0)
                throw new InvalidOperationException(
                    "OpenTelemetry endpoint configuration is malformed: " + string.Join("; ", rejected)
                    + ". Endpoints must be absolute http/https URIs, for example http://localhost:4318."
                    + " Leave a value blank to fall back to the OTLP endpoint.");
        }

#if !NETFRAMEWORK
        public OtlpSettings Otlp { get; set; } = new OtlpSettings();
#endif

        public abstract string ServiceName { get; }
        public abstract string Version { get; }

        private Dictionary<string, object>? _cachedAttributes;

        public Dictionary<string, object> Attributes
        {
            get
            {
                return _cachedAttributes ??= new Dictionary<string, object>()
                {
                    ["service.name"] = ServiceName,
                    ["entity.name"] = ServiceName,
                    ["service.version"] = Version,
                    ["environment.name"] = string.IsNullOrWhiteSpace(EnvironmentName) ? Environment.MachineName : EnvironmentName,
                    ["instance.name"] = string.IsNullOrWhiteSpace(InstanceName) ? Environment.MachineName : InstanceName,
                    ["hostname"] = string.IsNullOrWhiteSpace(InstanceName) ? Environment.MachineName : InstanceName
                };
            }
        }

#if NETFRAMEWORK
        public abstract Uri? GetOtlpEndpoint();

        protected abstract string? GetHeaders();
#else
        // Configuration only. Environment resolution is layered on in OtlpResolutionExtensions, so
        // per-signal variables can take precedence over the generic ones as the spec requires.
        public virtual Uri? GetOtlpEndpoint()
        {
            return Otlp?.Endpoint;
        }

        protected virtual string? GetHeaders()
        {
            return Otlp?.Headers;
        }
#endif

        /// <inheritdoc />
        public virtual string? GetOtlpHeaders() => GetHeaders();

        public virtual string? GetApiKey()
        {
            // Generic variable only: a credential lookup has no signal to scope to.
            var headers = OtlpEnvironment.ResolveHeaders(GetOtlpHeaders(), OtlpEnvironment.SystemReader);
            if (string.IsNullOrWhiteSpace(headers))
                return null;

            var pairs = headers!
                            .Split(HeaderSeparators, StringSplitOptions.RemoveEmptyEntries)
                            .Select(p => p.Trim());

            foreach (var pair in pairs)
            {
                var kv = pair.Split(KvSeparator, 2);
                if (kv.Length != 2)
                    continue;

                var name = kv[0].Trim();
                var value = kv[1].Trim();

                var key = TryExtractApiKey(name, value);
                if (key != null)
                    return key;
            }

            return null;
        }

        private string? TryExtractApiKey(string name, string value)
        {
            if (name.Equals("api-key", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(value))
                return value;

            if (ServiceProvider == OpenTelemetryServiceProvider.Dynatrace
                && name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                return TryExtractDynatraceToken(value);
            }

            return null;
        }

        private static string? TryExtractDynatraceToken(string value)
        {
            const string prefix = "Api-Token ";
            // The caller trims the value, so text after the prefix is never blank.
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return value.Substring(prefix.Length).Trim();

            return string.IsNullOrEmpty(value) ? null : value;
        }
    }
}
