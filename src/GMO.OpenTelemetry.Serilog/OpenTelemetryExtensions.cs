using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace GMO.OpenTelemetry.Serilog
{
    public static class OpenTelemetryExtensions
    {
        /// <summary>
        /// Attaches the OTLP log pipeline, resolving <see cref="IAttributeEnricher"/> from the container.
        /// </summary>
        public static void AddOpenTelemetry(this LoggerConfiguration logConfig, IOpenTelemetryOptions options, IConfiguration configuration, IServiceProvider services)
        {
#if NET6_0_OR_GREATER
            ArgumentNullException.ThrowIfNull(services);
#else
            if (services == null) throw new ArgumentNullException(nameof(services));
#endif

            // Guard before resolving: a disabled pipeline must not require IAttributeEnricher to be registered.
            if (!options.Enabled || !options.EnableLogging)
                return;

            logConfig.AddOpenTelemetryWithEnricher(options, configuration, services.GetRequiredService<IAttributeEnricher>());
        }

        /// <summary>
        /// Attaches the OTLP log pipeline without an <see cref="IServiceProvider"/>, for hosts that build
        /// their logger before the container exists. Uses a default <see cref="AttributeEnricher"/> wired to
        /// a <see cref="CorrelationIdService"/> so log events still carry a correlation ID.
        /// </summary>
        public static void AddOpenTelemetry(this LoggerConfiguration logConfig, IOpenTelemetryOptions options, IConfiguration configuration)
        {
            if (!options.Enabled || !options.EnableLogging)
                return;

            logConfig.AddOpenTelemetryWithEnricher(options, configuration, new AttributeEnricher(options, new CorrelationIdService()));
        }

        /// <summary>
        /// Attaches the OTLP log pipeline using an explicitly supplied <see cref="IAttributeEnricher"/>.
        /// </summary>
        public static void AddOpenTelemetryWithEnricher(this LoggerConfiguration logConfig, IOpenTelemetryOptions options, IConfiguration configuration, IAttributeEnricher attributeEnricher)
        {
#if NET6_0_OR_GREATER
            ArgumentNullException.ThrowIfNull(attributeEnricher);
#else
            if (attributeEnricher == null) throw new ArgumentNullException(nameof(attributeEnricher));
#endif

            if (!options.Enabled || !options.EnableLogging)
                return;

            (options as OpenTelemetryOptionsBase)?.ThrowIfEndpointsMalformed();

            // Applies to every sink on this logger: each level of a {@...} object runs its getters and
            // becomes log fields. Serilog's own default is 10.
            ObjectRenderer.Configure(options);
            logConfig.Destructure.ToMaximumDepth(ObjectRenderer.MaxDepth);

            // Enrichment is worth keeping regardless: it stamps trace/span ids onto events bound for
            // console, file and any other sink.
            OpenTelemetryEnricher.Configure(attributeEnricher);
            logConfig.Enrich.With<OpenTelemetryEnricher>();

            // The sink constructor throws the same way; failing here names the signal before any
            // partially-built pipeline exists, and covers net48, which never calls AddGmoOpenTelemetry.
            var chunkingSink = new ChunkingOpenTelemetrySink(options, configuration);
            logConfig.WriteTo.Sink(chunkingSink);

            // Export failures from the SDK, the exporters and this sink become marked log events, which
            // reach every sink but the OTLP one.
            SerilogTelemetryDiagnostics.Attach();
        }
    }
}
