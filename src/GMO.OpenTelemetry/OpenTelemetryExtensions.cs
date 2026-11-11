using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace GMO.OpenTelemetry
{
    public static class OpenTelemetryExtensions
    {
        // OTLP HTTP paths are URI segments (always '/'), not OS directory separators.
        private static readonly char OtlpUriPathSeparator = Path.AltDirectorySeparatorChar;

        private static readonly string[] ExistingOtlpSignalPaths =
        {
            OtlpUriPathSeparator + "v1/traces",
            OtlpUriPathSeparator + "v1/metrics",
            OtlpUriPathSeparator + "v1/logs",
        };

#if NET
        public static IOpenTelemetryBuilder AddGmoOpenTelemetry(
            this IServiceCollection services,
            IOpenTelemetryOptions options,
            Action<TracerProviderBuilder>? additionalTracing = null,
            Action<MeterProviderBuilder>? additionalMetrics = null)
        {
            if (!options.Enabled)
                return services.AddOpenTelemetry();

            if (options.Otlp == null)
                throw new InvalidOperationException(
                    "OtlpSettings must be configured when OpenTelemetry is enabled");

            services.Configure<OtlpExporterOptions>(opts =>
            {
                options.Otlp.ApplyTo(opts);
                opts.ExportProcessorType = ExportProcessorType.Batch;
            });

            services.AddSingleton<TruncatingSpanProcessor>();
            services.AddSingleton<CorrelationIdProcessor>();
            // AttributeEnricher resolves this; without it every host has to register the options itself.
            services.TryAddSingleton<IOpenTelemetryOptions>(options);
            services.TryAddSingleton<IAttributeEnricher, AttributeEnricher>();

            var otelBuilder = services.AddOpenTelemetry();
            otelBuilder.ConfigureResource(b => b
                .AddService(options.ServiceName, serviceVersion: options.Version)
                .AddEnvironmentVariableDetector()
                .AddTelemetrySdk()
                .AddAttributes(options.Attributes)
            );

            otelBuilder.WithGmoTracing(options, additionalTracing);
            services.AddMetrics(otelBuilder, options, additionalMetrics ?? (_ => { }));

            return otelBuilder;
        }

        public static IOpenTelemetryBuilder WithGmoTracing(
            this IOpenTelemetryBuilder builder,
            IOpenTelemetryOptions options,
            Action<TracerProviderBuilder>? configure = null)
        {
            ObjectRenderer.Configure(options);
            TelemetryDiagnostics.Start();

            if (options.TraceSourceNames.Length == 0)
                return builder;

            // Resolved once and captured: resolving again inside the callback re-reads the environment
            // at provider-build time, so the value validated here need not be the one exported to.
            var endpoint = options.ResolveOtlpEndpoint(OtlpSignal.Traces);
            var protocol = options.ResolveOtlpProtocol(OtlpSignal.Traces);

            // Gated on Enabled because this method is public and may be called directly.
            if (options.Enabled)
            {
                (options as OpenTelemetryOptionsBase)?.ThrowIfEndpointsMalformed();

                if (endpoint == null)
                    throw OtlpEnvironment.MissingEndpoint("Traces", "Telemetry:TraceSourceNames to an empty array",
                        "Telemetry:Otlp:Endpoint",
                        OtlpEnvironment.Variable(OtlpSignal.Traces, "ENDPOINT"),
                        "OTEL_EXPORTER_OTLP_ENDPOINT");
            }

            builder.WithTracing(tracing =>
            {
                tracing
                    .SetSampler(new AlwaysOnSampler())
                    .AddSource(options.TraceSourceNames)
                    .AddOtlpExporter(opt =>
                    {
                        // Assigning Endpoint clears AppendSignalPathToEndpoint (internal). For HttpProtobuf
                        // include the signal path so we don't POST to "/" (404).
                        if (protocol == OtlpExportProtocol.HttpProtobuf && endpoint != null)
                            opt.Endpoint = AppendOtlpSignalPath(endpoint, "v1/traces");

                        ConfigureTracesExport(opt, options);
                    })
                    .AddProcessor<TruncatingSpanProcessor>()
                    .AddProcessor<CorrelationIdProcessor>();

                if (options.EnableHttpInstrumentation)
                    tracing.AddHttpClientInstrumentation(x => x.RecordException = options.EnableHttpRecordException);

                if (options.EnableConsole)
                    tracing.AddConsoleExporter();

                configure?.Invoke(tracing);
            });

            return builder;
        }
#endif

#if NETFRAMEWORK
        public static TracerProviderBuilder AddGmoInstrumentation(
            this TracerProviderBuilder builder,
            IOpenTelemetryOptions options)
        {
            ObjectRenderer.Configure(options);
            TelemetryDiagnostics.Start();

            // This method never checked Enabled, so telemetry switched off still wired a live exporter.
            if (!options.Enabled)
                return builder;

            var endpoint = options.ResolveOtlpEndpoint(OtlpSignal.Traces);

            if (options.TraceSourceNames.Length > 0)
            {
                (options as OpenTelemetryOptionsBase)?.ThrowIfEndpointsMalformed();

                if (endpoint == null)
                    throw OtlpEnvironment.MissingEndpoint("Traces", "Telemetry:TraceSourceNames to an empty array",
                        "Telemetry:Otlp:Endpoint",
                        OtlpEnvironment.Variable(OtlpSignal.Traces, "ENDPOINT"),
                        "OTEL_EXPORTER_OTLP_ENDPOINT");
            }

            builder
                .SetSampler(new AlwaysOnSampler())
                .AddSource(options.TraceSourceNames)
                .ConfigureResource(b => b
                    .AddService(options.ServiceName, serviceVersion: options.Version)
                    .AddEnvironmentVariableDetector()
                    .AddTelemetrySdk()
                    .AddAttributes(options.Attributes)
                );

            if (endpoint != null)
                builder.AddOtlpExporter(opt =>
                {
                    // Assigning Endpoint clears AppendSignalPathToEndpoint (internal); embed /v1/traces.
                    opt.Endpoint = AppendOtlpSignalPath(endpoint, "v1/traces");
                    opt.Protocol = OtlpExportProtocol.HttpProtobuf;
                    // Prefer raw headers so Dynatrace Authorization=Api-Token is preserved.
                    var headers = options.ResolveOtlpHeadersFor(OtlpSignal.Traces);
                    if (!string.IsNullOrWhiteSpace(headers))
                        opt.Headers = headers;
                    ConfigureTracesExport(opt, options);
                });

            if (options.EnableHttpInstrumentation)
                builder.AddHttpClientInstrumentation();

            if (options.EnableConsole)
                builder.AddConsoleExporter();

            return builder;
        }
#endif

        public static void AddMetrics(this IServiceCollection services,
            IOpenTelemetryBuilder builder,
            IOpenTelemetryOptions options,
            Action<MeterProviderBuilder> configure)
        {
            if (options.Enabled && options.MeterSourceNames.Length > 0)
            {
#if NET
                services.AddHostedService<CustomMetrics>();
#endif
                builder.WithMetrics(options, configure);
            }
        }

        /// <summary>
        /// Builds an OTLP HTTP endpoint that already includes the signal path.
        /// Assigning <see cref="OtlpExporterOptions.Endpoint"/> clears the internal
        /// <c>AppendSignalPathToEndpoint</c> flag, so HttpProtobuf would otherwise POST to <c>/</c> (404).
        /// When the URI already ends with a different signal path (e.g. <c>/v1/traces</c> while
        /// requesting metrics), that segment is replaced so signals are not cross-posted.
        /// Query strings and fragments are preserved.
        /// </summary>
        public static Uri AppendOtlpSignalPath(Uri endpoint, string signalRelativePath)
        {
#if NET
            ArgumentNullException.ThrowIfNull(endpoint);
#else
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
#endif
            if (string.IsNullOrWhiteSpace(signalRelativePath))
                throw new ArgumentNullException(nameof(signalRelativePath));

            var builder = new UriBuilder(endpoint);
            var path = string.IsNullOrEmpty(builder.Path) || builder.Path == OtlpUriPathSeparator.ToString()
                ? string.Empty
                : builder.Path.TrimEnd(OtlpUriPathSeparator);
            var signalPath = OtlpUriPathSeparator + signalRelativePath.TrimStart(OtlpUriPathSeparator);

            if (path.EndsWith(signalPath, StringComparison.OrdinalIgnoreCase))
                return endpoint;

            var matching = ExistingOtlpSignalPaths.FirstOrDefault(
                suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (matching != null)
                path = path.Substring(0, path.Length - matching.Length).TrimEnd(OtlpUriPathSeparator);

            builder.Path = path + signalPath;
            return builder.Uri;
        }

        /// <summary>Batching, timeouts and compression for a trace exporter, from the options.</summary>
        internal static void ConfigureTracesExport(OtlpExporterOptions opt, IOpenTelemetryOptions options)
        {
            opt.BatchExportProcessorOptions.MaxQueueSize = options.TracesMaxQueueSize;
            opt.BatchExportProcessorOptions.ScheduledDelayMilliseconds = options.TracesExportDelayMs;
            opt.BatchExportProcessorOptions.MaxExportBatchSize = options.TracesMaxBatchSize;
            opt.BatchExportProcessorOptions.ExporterTimeoutMilliseconds = options.TracesExportTimeoutMs;
            // The batch timeout does not bound the request: that is TimeoutMilliseconds, which stays at the
            // SDK's 10 s otherwise, and a request exceeding it loses its whole batch.
            opt.TimeoutMilliseconds = options.TracesExportTimeoutMs;
            opt.ApplyGzipCompression(options);
        }

        /// <summary>
        /// Sets the exporter's own compression from <see cref="IOpenTelemetryOptions.EnableGzipCompression"/>.
        /// Call it from any OTLP exporter a host configures itself; the exporters this library builds
        /// already do.
        /// </summary>
        /// <remarks>
        /// Always set explicitly, so the option overrides OTEL_EXPORTER_OTLP_COMPRESSION rather than
        /// stacking on it. Compressing in an HttpClientFactory handler instead fails both ways: on .NET 5+
        /// the exporter sends synchronously, which bypasses a handler's SendAsync, so nothing is
        /// compressed; on .NET Framework it runs, and with the variable also set the body is gzipped twice.
        /// </remarks>
        public static OtlpExporterOptions ApplyGzipCompression(this OtlpExporterOptions exporter, IOpenTelemetryOptions options)
        {
#if NET
            ArgumentNullException.ThrowIfNull(exporter);
            ArgumentNullException.ThrowIfNull(options);
#else
            if (exporter == null) throw new ArgumentNullException(nameof(exporter));
            if (options == null) throw new ArgumentNullException(nameof(options));
#endif
            exporter.Compression = options.EnableGzipCompression ? OtlpExportCompression.GZip : OtlpExportCompression.None;
            return exporter;
        }

        /// <summary>
        /// Applies the <c>Logs*</c> batching options to a log exporter the host builds itself, such as the
        /// one behind the log4net appender. Without it the SDK sends 512 records per request, and New
        /// Relic rejects a request over 1 MB whole - every record in it, not just the large one.
        /// </summary>
        public static LogRecordExportProcessorOptions ApplyLogsBatching(this LogRecordExportProcessorOptions processor, IOpenTelemetryOptions options)
        {
#if NET
            ArgumentNullException.ThrowIfNull(processor);
            ArgumentNullException.ThrowIfNull(options);
#else
            if (processor == null) throw new ArgumentNullException(nameof(processor));
            if (options == null) throw new ArgumentNullException(nameof(options));
#endif
            var batch = processor.BatchExportProcessorOptions;
            batch.MaxExportBatchSize = options.LogsMaxBatchSize > 0 ? options.LogsMaxBatchSize : OpenTelemetryOptionsBase.DefaultLogsMaxBatchSize;
            if (options.LogsMaxQueueSize > 0)
                batch.MaxQueueSize = Math.Max(options.LogsMaxQueueSize, batch.MaxExportBatchSize);
            if (options.LogsExportDelayMs > 0)
                batch.ScheduledDelayMilliseconds = options.LogsExportDelayMs;
            if (options.LogsExportTimeoutMs > 0)
                batch.ExporterTimeoutMilliseconds = options.LogsExportTimeoutMs;
            return processor;
        }

        public static void WithMetrics(this IOpenTelemetryBuilder builder, IOpenTelemetryOptions options, Action<MeterProviderBuilder> configure)
        {
            if (!options.Enabled || options.MeterSourceNames.Length == 0) return;

            // Resolved out here so a missing endpoint fails at registration rather than at provider build.
            var endpoint = ResolveMetricsEndpointOrThrow(options);

            builder.WithMetrics(metrics =>
            {
                configure?.Invoke(metrics);
                metrics.AddGmoMetricsCore(options, endpoint);
            });
        }

        /// <summary>
        /// The GMO metrics pipeline for a <see cref="MeterProvider"/> built by hand — instrumentation,
        /// meters, OTLP exporter, delta temporality and resource. Framework hosts cannot reach
        /// <see cref="AddMetrics"/> or <see cref="WithMetrics"/>, both of which need DI, so without this
        /// they hand-roll the wiring and drift from what the package does.
        /// </summary>
        public static MeterProviderBuilder AddGmoMetrics(
            this MeterProviderBuilder builder, IOpenTelemetryOptions options)
        {
#if NET
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(options);
#else
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (options == null) throw new ArgumentNullException(nameof(options));
#endif

            if (!options.Enabled || options.MeterSourceNames.Length == 0) return builder;

            TelemetryDiagnostics.Start();

            return builder.AddGmoMetricsCore(options, ResolveMetricsEndpointOrThrow(options));
        }

        private static Uri ResolveMetricsEndpointOrThrow(IOpenTelemetryOptions options)
        {
            (options as OpenTelemetryOptionsBase)?.ThrowIfEndpointsMalformed();

            var endpoint = options.MetricsEndpoint ?? options.ResolveOtlpEndpoint(OtlpSignal.Metrics);
            if (endpoint == null)
                throw OtlpEnvironment.MissingEndpoint("Metrics", "Telemetry:MeterSourceNames to an empty array",
                    "Telemetry:MetricsEndpoint",
                    "Telemetry:Otlp:Endpoint",
                    OtlpEnvironment.Variable(OtlpSignal.Metrics, "ENDPOINT"),
                    "OTEL_EXPORTER_OTLP_ENDPOINT");

            OtlpEnvironment.WarnIfSignalEndpointMismatched(
                options, OtlpSignal.Metrics, endpoint, nameof(OpenTelemetryExtensions));

            return endpoint;
        }

        private static MeterProviderBuilder AddGmoMetricsCore(
            this MeterProviderBuilder builder, IOpenTelemetryOptions options, Uri endpoint)
        {
            builder
                .ConfigureResource(b => b
                    .AddService(options.ServiceName, serviceVersion: options.Version)
                    .AddAttributes(options.Attributes))
                .AddRuntimeInstrumentation()
                .AddProcessInstrumentation()
                .AddMeter(CustomMetrics.Name)
                .AddMeter(options.MeterSourceNames)
                .AddView(CustomMetrics.DropMisdeclaredRuntimeInstruments)
                .AddOtlpExporter((opts, reader) =>
                {
                    ConfigureMetricsOtlpExporter(opts, reader, options, endpoint);
                    // Dynatrace discards cumulative counters and histograms outright, and New Relic
                    // only accepts them by converting to delta on ingest. Set on the reader, so a
                    // consumer's own exporter (Prometheus wants cumulative) keeps its own preference.
                    reader.TemporalityPreference = MetricReaderTemporalityPreference.Delta;
                });

            if (options.EnableConsole)
                builder.AddConsoleExporter();

            return builder;
        }

        /// <summary>
        /// Resolves metrics OTLP protocol. Framework is always HttpProtobuf - its HttpClient has no
        /// HTTP/2 support, so OTLP/gRPC cannot work there. On modern TFMs, honour the resolved
        /// Otlp protocol unless MetricsEndpoint implies HTTP (:4318 or /v1/metrics).
        /// </summary>
        internal static OtlpExportProtocol ResolveMetricsExportProtocol(IOpenTelemetryOptions options)
        {
#if NET
            if (options.MetricsEndpoint != null
                && (options.MetricsEndpoint.Port == 4318
                    || options.MetricsEndpoint.AbsolutePath.Contains("/v1/metrics", StringComparison.OrdinalIgnoreCase)))
            {
                return OtlpExportProtocol.HttpProtobuf;
            }

            return options.ResolveOtlpProtocol(OtlpSignal.Metrics);
#else
            return OtlpExportProtocol.HttpProtobuf;
#endif
        }

        /// <summary>
        /// Configures metrics OTLP exporter endpoint, protocol, headers, batching, and optional gzip.
        /// </summary>
        internal static void ConfigureMetricsOtlpExporter(
            OtlpExporterOptions opts,
            MetricReaderOptions? reader,
            IOpenTelemetryOptions options,
            Uri endpoint)
        {
            var protocol = ResolveMetricsExportProtocol(options);
            opts.Protocol = protocol;
            // Assigning Endpoint clears AppendSignalPathToEndpoint (internal).
            // Only embed /v1/metrics when exporting via HttpProtobuf; gRPC uses the host as-is.
            opts.Endpoint = protocol == OtlpExportProtocol.HttpProtobuf
                ? AppendOtlpSignalPath(endpoint, "v1/metrics")
                : endpoint;

            // Prefer raw headers so Dynatrace Authorization=Api-Token is preserved. Always assign: the
            // exporter pre-fills Headers from OTEL_EXPORTER_OTLP_HEADERS, which must not beat configuration.
            var headers = options.ResolveOtlpHeadersFor(OtlpSignal.Metrics);
            if (!string.IsNullOrWhiteSpace(headers))
                opts.Headers = headers;

            // Metrics are pulled on an interval, not queued and batched, so they never read
            // BatchExportProcessorOptions - setting it here left all four Metrics* options inert.
            // MetricsMaxQueueSize and MetricsMaxBatchSize have no metric equivalent at all.
            if (reader != null)
            {
                reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = options.MetricsExportDelayMs;
                reader.PeriodicExportingMetricReaderOptions.ExportTimeoutMilliseconds = options.MetricsExportTimeoutMs;
                // The reader's timeout does not bound the request; this does, and otherwise stays at 10 s.
                opts.TimeoutMilliseconds = options.MetricsExportTimeoutMs;
            }

            opts.ApplyGzipCompression(options);
        }
    }
}
