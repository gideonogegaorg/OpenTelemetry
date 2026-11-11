using System;
using Serilog.Core;
using Serilog.Events;

namespace GMO.OpenTelemetry.Serilog
{
    public class OpenTelemetryEnricher : ILogEventEnricher
    {
        public const string LevelProp = "level";

        // Static service locator pattern for Serilog compatibility.
        // volatile: logging may be configured off the main thread, and a stale null here silently
        // skips enrichment rather than failing.
        private static volatile IAttributeEnricher? _attributeEnricher;

        // Parameterless constructor required by Serilog
        public OpenTelemetryEnricher()
        {
        }

        // Static method to configure the enricher with dependencies
        public static void Configure(IAttributeEnricher attributeEnricher)
        {
            _attributeEnricher = attributeEnricher;
        }

        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            // Whole body guarded: enrichment runs on the caller's thread, so nothing here may surface
            // as a failure of the operation being logged.
            try
            {
                if (!logEvent.Properties.ContainsKey(LevelProp))
                {
                    var levelProp = propertyFactory.CreateProperty(LevelProp, logEvent.Level.ToString());
                    logEvent.AddPropertyIfAbsent(levelProp);
                }

                if (_attributeEnricher == null) return;

                var attributes = _attributeEnricher.Enrich();
                if (attributes == null) return;

                foreach (var attr in attributes)
                {
                    var prop = propertyFactory.CreateProperty(attr.Key, attr.Value);
                    logEvent.AddOrUpdateProperty(prop);
                }
            }
            catch (Exception ex)
            {
                GmoTelemetryEventSource.Log.OperationFailed(nameof(OpenTelemetryEnricher),
                    "enriching log event", GmoTelemetryEventSource.Describe(ex));
            }
        }
    }
}
