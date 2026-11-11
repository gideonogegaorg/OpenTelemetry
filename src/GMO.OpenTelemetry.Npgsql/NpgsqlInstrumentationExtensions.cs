using System.Diagnostics;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace GMO.OpenTelemetry.Npgsql
{
    public static class NpgsqlInstrumentationExtensions
    {
        public const string ActivitySourceName = "Npgsql";
        public const string MeterName = "Npgsql";

        private const string Component = "NpgsqlInstrumentation";

        public static TracerProviderBuilder AddNpgsqlInstrumentationWithEnrichment(this TracerProviderBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            return builder.AddNpgsql();
        }

        public static MeterProviderBuilder AddNpgsqlMetrics(this MeterProviderBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            return builder.AddNpgsqlInstrumentation();
        }

        public static NpgsqlDataSourceBuilder ConfigureTracingWithEnrichment(
            this NpgsqlDataSourceBuilder builder,
            bool enableTextEnrichment = false,
            Func<NpgsqlCommand, bool>? commandFilter = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            return builder.ConfigureTracing(t => t.UseEnrichment(enableTextEnrichment, commandFilter));
        }

        public static NpgsqlSlimDataSourceBuilder ConfigureTracingWithEnrichment(
            this NpgsqlSlimDataSourceBuilder builder,
            bool enableTextEnrichment = false,
            Func<NpgsqlCommand, bool>? commandFilter = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            return builder.ConfigureTracing(t => t.UseEnrichment(enableTextEnrichment, commandFilter));
        }

        public static NpgsqlTracingOptionsBuilder UseEnrichment(
            this NpgsqlTracingOptionsBuilder options,
            bool enableTextEnrichment = false,
            Func<NpgsqlCommand, bool>? commandFilter = null)
        {
            ArgumentNullException.ThrowIfNull(options);

            if (enableTextEnrichment)
            {
                options.ConfigureCommandEnrichmentCallback(EnrichWithNpgsqlCommand);
                options.ConfigureBatchEnrichmentCallback(EnrichWithNpgsqlBatch);
            }

            if (commandFilter != null)
                options.ConfigureCommandFilter(commandFilter);

            return options;
        }

        private static void EnrichWithNpgsqlCommand(Activity activity, NpgsqlCommand command) =>
            DbCommandEnricher.Enrich(activity, command, Component);

        private static void EnrichWithNpgsqlBatch(Activity activity, NpgsqlBatch batch) =>
            DbCommandEnricher.Enrich(activity, batch, Component);
    }
}
