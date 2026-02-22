using Serilog.Core;
using Serilog.Events;

namespace GMO.OpenTelemetry.Serilog
{
    public class OpenTelemetryEnricher : ILogEventEnricher
    {
        public const string LevelProp = "level";

        // Static service locator pattern for Serilog compatibility
        private static IAttributeEnricher _attributeEnricher;

        // Parameterless constructor required by Serilog
        public OpenTelemetryEnricher()
        {
        }

        // Static method to configure the enricher with dependencies
        public static void Configure(IAttributeEnricher attributeEnricher)
        {
            _attributeEnricher = attributeEnricher;
        }

        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory factory)
        {
            // Add log level
            if (!logEvent.Properties.ContainsKey(LevelProp))
            {
                var levelProp = factory.CreateProperty(LevelProp, logEvent.Level.ToString());
                logEvent.AddPropertyIfAbsent(levelProp);
            }

            // Only enrich if the service is configured
            if (_attributeEnricher != null)
            {
                // Use shared enrichment service with source location enabled for Serilog
                var attributes = _attributeEnricher.Enrich();

                // Add all enriched attributes to the log event
                foreach (var attr in attributes)
                {
                    var prop = factory.CreateProperty(attr.Key, attr.Value);
                    logEvent.AddOrUpdateProperty(prop);
                }

                // Add custom properties from logging context
                AddCustomProperties(logEvent, factory, attributes.Keys);
            }
        }

        private void AddCustomProperties(LogEvent logEvent, ILogEventPropertyFactory factory, ICollection<string> existingKeys)
        {
            try
            {
                var reservedKeys = new HashSet<string>(existingKeys, StringComparer.OrdinalIgnoreCase)
                {
                    LevelProp // Also exclude the level property we added
                };

                foreach (var property in logEvent.Properties)
                {
                    var key = property.Key;

                    if (!key.StartsWith("Serilog") && !reservedKeys.Contains(key))
                    {
                        var prefixedKey = $"property.{key}";
                        if (!logEvent.Properties.ContainsKey(prefixedKey))
                        {
                            var value = property.Value?.ToString();
                            if (!string.IsNullOrEmpty(value))
                            {
                                var customProp = factory.CreateProperty(prefixedKey, value);
                                logEvent.AddOrUpdateProperty(customProp);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Silently ignore custom property errors
            }
        }
    }
}
