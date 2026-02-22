using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace GMO.OpenTelemetry
{
    public static class OpenTelemetryExtensions
    {
        public static void AddMetrics(this IServiceCollection services,
            IOpenTelemetryBuilder builder,
            IOpenTelemetryOptions options,
            Action<MeterProviderBuilder> configure)
        {
            if (options.Enabled && options.MeterSourceNames.Any())
            {
                services.AddHostedService<CustomMetrics>();
                builder.WithMetrics(options, configure);
            }
        }

        public static void WithMetrics(this IOpenTelemetryBuilder builder, IOpenTelemetryOptions options, Action<MeterProviderBuilder> configure)
        {
            if (!options.Enabled || !options.MeterSourceNames.Any()) return;

            var endpoint = options.MetricsEndpoint ?? options.GetOtlpEndpoint();
            if (endpoint == null) return;

            builder.WithMetrics(metrics =>
            {
                configure?.Invoke(metrics);
                var metricsProvider = metrics
                 .AddRuntimeInstrumentation()
                 .AddProcessInstrumentation()
                 .AddMeter(CustomMetrics.Name)
                 .AddMeter(options.MeterSourceNames)
                 .AddOtlpExporter(opts =>
                 {
                     opts.Endpoint = endpoint;
                     opts.BatchExportProcessorOptions.MaxQueueSize = options.MetricsMaxQueueSize;
                     opts.BatchExportProcessorOptions.ScheduledDelayMilliseconds = options.MetricsExportDelayMs;
                     opts.BatchExportProcessorOptions.ExporterTimeoutMilliseconds = options.MetricsExportTimeoutMs;
                     opts.BatchExportProcessorOptions.MaxExportBatchSize = options.MetricsMaxBatchSize;
                 });

                if (options.EnableConsole)
                    metricsProvider.AddConsoleExporter();
            });
        }
    }
}
