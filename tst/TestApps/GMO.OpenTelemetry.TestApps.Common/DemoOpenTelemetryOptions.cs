using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

namespace GMO.OpenTelemetry.TestApps.Common
{
    public sealed class DemoOpenTelemetryOptions(string serviceName, string? version = null) : OpenTelemetryOptionsBase
    {
        private readonly string _serviceName = serviceName;
        private readonly string _version = version ?? "1.0.0-testapp";

#if NETFRAMEWORK
        private Uri? _otlpEndpoint;
        private string? _headers;
#endif

        public override string ServiceName => _serviceName;
        public override string Version => _version;

        public string? SqlConnectionString { get; set; }

        public string? PostgresConnectionString { get; set; }

#if NETFRAMEWORK
        public override Uri? GetOtlpEndpoint() => _otlpEndpoint;

        protected override string? GetHeaders() => _headers;

        public void SetOtlpEndpoint(Uri? endpoint) => _otlpEndpoint = endpoint;

        public void SetHeaders(string? headers) => _headers = headers;
#endif

        public static DemoOpenTelemetryOptions FromConfiguration(IConfiguration configuration, string serviceName)
        {
            var section = configuration.GetSection("Telemetry");
            var options = new DemoOpenTelemetryOptions(serviceName);
            section.Bind(options);

            // Bind does not always set abstract-backed endpoint fields on Framework.
#if NETFRAMEWORK
            var endpoint = section["OtlpExportEndpoint"]
                ?? section["Otlp:Endpoint"]
                ?? section.GetSection("Otlp")["Endpoint"];
            if (!string.IsNullOrWhiteSpace(endpoint) && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
                options.SetOtlpEndpoint(uri);

            var headers = section["Headers"]
                ?? section["Otlp:Headers"]
                ?? section.GetSection("Otlp")["Headers"];
            options.SetHeaders(headers);
#else
            // The configuration binder does not reliably convert the Otlp:Endpoint string into the
            // Uri property, so read Otlp settings explicitly (mirrors the net48 path).
            options.Otlp ??= new OtlpSettings();
            var endpoint = section["Otlp:Endpoint"] ?? section.GetSection("Otlp")["Endpoint"];
            if (!string.IsNullOrWhiteSpace(endpoint) && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
                options.Otlp.Endpoint = uri;
            var headers = section["Otlp:Headers"] ?? section.GetSection("Otlp")["Headers"];
            if (!string.IsNullOrWhiteSpace(headers))
                options.Otlp.Headers = headers;
            var protocol = section["Otlp:Protocol"] ?? section.GetSection("Otlp")["Protocol"];
            if (!string.IsNullOrWhiteSpace(protocol)
                && Enum.TryParse<global::OpenTelemetry.Exporter.OtlpExportProtocol>(protocol, ignoreCase: true, out var proto))
                options.Otlp.Protocol = proto;
#endif

            var metricsEndpoint = section["MetricsEndpoint"];
            if (!string.IsNullOrWhiteSpace(metricsEndpoint) && Uri.TryCreate(metricsEndpoint, UriKind.Absolute, out var metricsUri))
                options.MetricsEndpoint = metricsUri;

            options.SqlConnectionString = section["SqlConnectionString"]
                ?? configuration["Telemetry:SqlConnectionString"];
            options.PostgresConnectionString = section["PostgresConnectionString"];

            if (options.TraceSourceNames == null || options.TraceSourceNames.Length == 0)
                options.TraceSourceNames = [serviceName];

            if (options.MeterSourceNames == null || options.MeterSourceNames.Length == 0)
                options.MeterSourceNames = [serviceName, CustomMetrics.Name];

            // Default an unconfigured TestApp at the local OTLP collector (docker compose) rather
            // than a vendor: ServiceProvider stays Collector (the enum default). net48 is
            // HTTP/protobuf on :4318; modern uses gRPC on :4317.
#if NETFRAMEWORK
            if (options.GetOtlpEndpoint() == null)
                options.SetOtlpEndpoint(new Uri("http://localhost:4318"));
#else
            options.Otlp ??= new OtlpSettings();
            if (options.Otlp.Endpoint == null)
                options.Otlp.Endpoint = new Uri("http://localhost:4317");
#endif

            return options;
        }

        public static IConfiguration BuildConfiguration(string? basePath = null)
        {
            var path = basePath ?? AppContext.BaseDirectory;
            return new ConfigurationBuilder()
                .SetBasePath(path)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
                .AddEnvironmentVariables()
                .Build();
        }
    }
}
