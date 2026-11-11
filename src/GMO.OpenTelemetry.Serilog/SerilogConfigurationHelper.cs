using System;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;

namespace GMO.OpenTelemetry.Serilog
{
    public static class SerilogConfigurationHelper
    {
        /// <summary>
        /// Reads Serilog configuration excluding the WriteTo sinks section
        /// </summary>
        public static LoggerConfiguration ReadSerilogConfigWithoutSinks(
            this LoggerConfiguration loggerConfig,
            IConfiguration configuration)
        {
#if NET6_0_OR_GREATER
            ArgumentNullException.ThrowIfNull(loggerConfig);
            ArgumentNullException.ThrowIfNull(configuration);
#else
            if (loggerConfig == null) throw new ArgumentNullException(nameof(loggerConfig));
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
#endif

            var serilogSection = configuration.GetSection("Serilog");

            if (!serilogSection.Exists())
                return loggerConfig;

            ApplyMinimumLevel(loggerConfig, serilogSection.GetSection("MinimumLevel"));

            var enrichSection = serilogSection.GetSection("Enrich");
            if (enrichSection.Exists())
                ApplyEnrichersFromConfiguration(loggerConfig, enrichSection);

            return loggerConfig;
        }

        private static void ApplyMinimumLevel(LoggerConfiguration loggerConfig, IConfigurationSection minimumLevelSection)
        {
            if (!minimumLevelSection.Exists())
                return;

            var defaultLevel = minimumLevelSection["Default"];
            if (!string.IsNullOrWhiteSpace(defaultLevel) &&
                Enum.TryParse<LogEventLevel>(defaultLevel, out var level))
            {
                loggerConfig.MinimumLevel.Is(level);
            }

            ApplyMinimumLevelOverrides(loggerConfig, minimumLevelSection.GetSection("Override"));
        }

        private static void ApplyMinimumLevelOverrides(LoggerConfiguration loggerConfig, IConfigurationSection overrideSection)
        {
            if (!overrideSection.Exists())
                return;

            foreach (var item in overrideSection.GetChildren())
            {
                if (!string.IsNullOrWhiteSpace(item.Key) &&
                    !string.IsNullOrWhiteSpace(item.Value) &&
                    Enum.TryParse<LogEventLevel>(item.Value, out var overrideLevel))
                {
                    loggerConfig.MinimumLevel.Override(item.Key, overrideLevel);
                }
            }
        }

        /// <summary>
        /// Dynamically applies enrichers using reflection to discover available methods
        /// </summary>
        private static void ApplyEnrichersFromConfiguration(
            LoggerConfiguration loggerConfig,
            IConfigurationSection enrichSection)
        {
            var enrichmentConfig = loggerConfig.Enrich;
            var enrichmentType = enrichmentConfig.GetType();

            foreach (var enricher in enrichSection.GetChildren())
            {
                var enricherName = enricher.Value;
                if (string.IsNullOrWhiteSpace(enricherName))
                {
                    continue;
                }

                try
                {
                    // Try to find a matching method on LoggerEnrichmentConfiguration
                    var method = enrichmentType.GetMethod(
                        enricherName,
                        BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance,
                        null,
                        Type.EmptyTypes,
                        null);

                    // Serilog's enrichment methods return the LoggerConfiguration, not the enrichment
                    // configuration; requiring the latter matched none of them.
                    if (method != null && typeof(LoggerConfiguration).IsAssignableFrom(method.ReturnType))
                    {
                        method.Invoke(enrichmentConfig, null);
                    }
                    else
                    {
                        GmoTelemetryEventSource.Log.Degraded(nameof(SerilogConfigurationHelper),
                            $"enricher method '{enricherName}' not found on LoggerEnrichmentConfiguration");
                    }
                }
                catch (Exception ex)
                {
                    GmoTelemetryEventSource.Log.OperationFailed(nameof(SerilogConfigurationHelper),
                        $"applying enricher '{enricherName}'", GmoTelemetryEventSource.Describe(ex));
                }
            }
        }
    }
}
