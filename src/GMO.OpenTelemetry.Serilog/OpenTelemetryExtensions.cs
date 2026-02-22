using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace GMO.OpenTelemetry.Serilog
{
    public static class OpenTelemetryExtensions
    {
        public static void AddOpenTelemetry(this LoggerConfiguration logConfig, IOpenTelemetryOptions options, IConfiguration configuration, IServiceProvider services)
        {
            if (options.Enabled && options.EnableLogging)
            {
                var attributeEnricher = services.GetRequiredService<IAttributeEnricher>();
                OpenTelemetryEnricher.Configure(attributeEnricher);
                logConfig.Enrich.With<OpenTelemetryEnricher>();

                var chunkingSink = new ChunkingOpenTelemetrySink(options, configuration);
                logConfig.WriteTo.Sink(chunkingSink);
            }
        }
    }
}
