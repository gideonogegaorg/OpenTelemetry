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
            // Add null checks for parameters
            if (loggerConfig == null)
                throw new ArgumentNullException(nameof(loggerConfig));
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));

            var serilogSection = configuration.GetSection("Serilog");

            if (serilogSection?.Exists() != true)
            {
                return loggerConfig;
            }

            // Configure MinimumLevel
            var minimumLevelSection = serilogSection.GetSection("MinimumLevel");
            if (minimumLevelSection?.Exists() == true)
            {
                var defaultLevel = minimumLevelSection["Default"];
                if (!string.IsNullOrWhiteSpace(defaultLevel) &&
                    Enum.TryParse<LogEventLevel>(defaultLevel, out var level))
                {
                    loggerConfig.MinimumLevel.Is(level);
                }

                // Configure overrides
                var overrideSection = minimumLevelSection.GetSection("Override");
                if (overrideSection?.Exists() == true)
                {
                    var children = overrideSection.GetChildren();
                    if (children != null)
                    {
                        foreach (var item in children)
                        {
                            if (!string.IsNullOrWhiteSpace(item.Key) &&
                                !string.IsNullOrWhiteSpace(item.Value) &&
                                Enum.TryParse<LogEventLevel>(item.Value, out var overrideLevel))
                            {
                                loggerConfig.MinimumLevel.Override(item.Key, overrideLevel);
                            }
                        }
                    }
                }
            }

            // Configure Enrichers using reflection
            var enrichSection = serilogSection.GetSection("Enrich");
            if (enrichSection?.Exists() == true)
            {
                ApplyEnrichersFromConfiguration(loggerConfig, enrichSection);
            }

            return loggerConfig;
        }

        /// <summary>
        /// Dynamically applies enrichers using reflection to discover available methods
        /// </summary>
        private static void ApplyEnrichersFromConfiguration(
            LoggerConfiguration loggerConfig,
            IConfigurationSection enrichSection)
        {
            // Add null checks
            if (loggerConfig == null)
                throw new ArgumentNullException(nameof(loggerConfig));
            if (enrichSection == null)
                throw new ArgumentNullException(nameof(enrichSection));

            var enrichmentConfig = loggerConfig.Enrich;
            if (enrichmentConfig == null)
                return;

            var enrichmentType = enrichmentConfig.GetType();
            if (enrichmentType == null)
                return;

            var children = enrichSection.GetChildren();
            if (children == null)
                return;

            foreach (var enricher in children)
            {
                var enricherName = enricher?.Value;
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

                    if (method != null && method.ReturnType == enrichmentType)
                    {
                        method.Invoke(enrichmentConfig, null);
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"Enricher method '{enricherName}' not found on LoggerEnrichmentConfiguration");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Failed to apply enricher '{enricherName}': {ex.Message}");
                }
            }
        }
    }
}
