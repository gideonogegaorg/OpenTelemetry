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
        bool EnableLogging { get; set; }
        OpenTelemetryServiceProvider ServiceProvider { get; set; }
        int MaxAttributeValueLength { get; set; }
        int MaxLogMessageLength { get; set; }
        bool SendOverflowLogs { get; set; }
        HashSet<string> OverflowLogOperations { get; set; }
        HashSet<string> AdditionalKeysToLog { get; set; }
        /// <summary>Application name (base name, e.g. "GMO.Family.Web"). Used for application.name attribute.</summary>
        string ApplicationName { get; }
        /// <summary>Service name (environment-suffixed when set, e.g. "GMO.Family.Web.main"). Used for service.name and resource.</summary>
        string ServiceName { get; }
        string Version { get; }
        Dictionary<string, object> Attributes { get; }
        bool EnableConsole { get; set; }

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

        string? LoggingEndpoint { get; set; }
        Uri? MetricsEndpoint { get; set; }
        OtlpExporterOptions Otlp { get; set; }

        Uri? GetOtlpEndpoint();
        string? GetApiKey();
    }

    public abstract class OpenTelemetryOptionsBase : IOpenTelemetryOptions
    {
        protected const int DefaultMaxAttributeValueLength = 4000;
        protected const int DefaultMaxLogMessageLength = 130000;

        public bool Enabled { get; set; } = false;
        public string EnvironmentName { get; set; } = "undefined"; // "local", "QA"...
        public string InstanceName { get; set; } = Environment.MachineName; // name of specific instance (eg. "Danko laptop", "John home PC")
        public string[] TraceSourceNames { get; set; } = new[] { "*" };
        public string[] MeterSourceNames { get; set; } = new[] { "*" };
        public bool EnableSqlInstrumentation { get; set; } = true;
        public bool EnableSqlInstrumentationText { get; set; } = true;
        public bool EnableHttpInstrumentation { get; set; } = true;
        public bool EnableLogging { get; set; } = true;
        public OpenTelemetryServiceProvider ServiceProvider { get; set; }
        public int MaxAttributeValueLength { get; set; } = DefaultMaxAttributeValueLength;
        public int MaxLogMessageLength { get; set; } = DefaultMaxLogMessageLength;
        public bool SendOverflowLogs { get; set; } = true;
        public HashSet<string> OverflowLogOperations { get; set; } = new HashSet<string>();
        public HashSet<string> AdditionalKeysToLog { get; set; } = new HashSet<string>();
        public bool EnableConsole { get; set; } = false;

        public bool IncludeSourceLocation { get; set; } = true;
        public bool IncludeThreadInfo { get; set; } = true;
        public bool IncludeUserInfo { get; set; } = true;

        public string[] LogStackExclusions { get; set; } = new string[0];

        public int TracesMaxQueueSize { get; set; } = 65536;
        public int TracesExportDelayMs { get; set; } = 10000;
        public int TracesExportTimeoutMs { get; set; } = 60000;
        public int TracesMaxBatchSize { get; set; } = 4096;

        public int MetricsMaxQueueSize { get; set; } = 8192;
        public int MetricsExportDelayMs { get; set; } = 15000;
        public int MetricsExportTimeoutMs { get; set; } = 60000;
        public int MetricsMaxBatchSize { get; set; } = 1024;

        public int LogsMaxQueueSize { get; set; } = 32768;
        public int LogsExportDelayMs { get; set; } = 5000;
        public int LogsExportTimeoutMs { get; set; } = 60000;
        public int LogsMaxBatchSize { get; set; } = 2048;

        public string? LoggingEndpoint { get; set; } = null;
        public Uri? MetricsEndpoint { get; set; } = null;
        public OtlpExporterOptions Otlp { get; set; } = new OtlpExporterOptions();

        public abstract string ApplicationName { get; }
        public abstract string Version { get; }

        private string? _serviceName;

        /// <summary>
        /// Service name (environment dot-suffixed when set, e.g. "GMO.Family.Web.main"). Used for service.name, entity.name, and resource.
        /// </summary>
        public virtual string ServiceName =>
            _serviceName ??= string.IsNullOrWhiteSpace(EnvironmentName) || string.Equals(EnvironmentName, "undefined", StringComparison.OrdinalIgnoreCase)
                ? ApplicationName
                : $"{ApplicationName}.{EnvironmentName.Trim()}";

        public Dictionary<string, object> Attributes => new Dictionary<string, object>()
        {
            ["service.name"] = ServiceName,
            ["application.name"] = ApplicationName,
            ["entity.name"] = ServiceName,
            ["service.version"] = Version,
            ["environment.name"] = string.IsNullOrWhiteSpace(EnvironmentName) ? Environment.MachineName : EnvironmentName,
            ["instance.name"] = string.IsNullOrWhiteSpace(InstanceName) ? Environment.MachineName : InstanceName,
            ["hostname"] = string.IsNullOrWhiteSpace(InstanceName) ? Environment.MachineName : InstanceName
        };

        public virtual Uri? GetOtlpEndpoint()
        {
            return Otlp.Endpoint;
        }

        protected virtual string? GetHeaders()
        {
            return Otlp.Headers;
        }

        public virtual string? GetApiKey()
        {
            var headers = GetHeaders();
            if (string.IsNullOrWhiteSpace(headers))
                return null;

            var pairs = headers
                            .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(p => p.Trim());

            foreach (var pair in pairs)
            {
                var kv = pair.Split(new[] { '=' }, 2);
                if (kv.Length != 2)
                    continue;

                var name = kv[0].Trim();
                var value = kv[1].Trim();

                if (name.Equals("api-key", StringComparison.OrdinalIgnoreCase)
                                && !string.IsNullOrEmpty(value))
                {
                    return value;
                }

                if (ServiceProvider == OpenTelemetryServiceProvider.Dynatrace
                                && name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    const string prefix = "Api-Token ";
                    if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var token = value.Substring(prefix.Length).Trim();
                        if (!string.IsNullOrEmpty(token))
                            return token;
                    }
                    else if (!string.IsNullOrEmpty(value))
                    {
                        return value;
                    }
                }
            }

            return null;
        }
    }
}
