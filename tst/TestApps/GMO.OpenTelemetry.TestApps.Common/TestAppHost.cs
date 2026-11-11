using System;
using System.Diagnostics;
#if NETFRAMEWORK
using System.Net;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
#else
using GMO.OpenTelemetry.Npgsql;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry.Trace;
#endif
using GMO.OpenTelemetry.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace GMO.OpenTelemetry.TestApps.Common
{
    public sealed class TestAppHost : IDisposable
    {
#if NETFRAMEWORK
        private readonly TracerProvider _tracerProvider;
        private readonly MeterProvider _meterProvider;
        private readonly CustomMetrics _customMetrics;
        private readonly ServiceProvider _serviceProvider;
#else
        private readonly IHost _host;
#endif

        public TestAppHost(DemoOpenTelemetryOptions options)
        {
            Options = options;
            ActivitySource = new ActivitySource(options.ServiceName);

#if NETFRAMEWORK
            // .NET Framework defaults can omit TLS 1.2; OTLP/HTTPS backends require it.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            CorrelationIdService = new CorrelationIdService();
            AttributeEnricher = new AttributeEnricher(options, CorrelationIdService);

            var services = new ServiceCollection();
            services.AddSingleton<ICorrelationIdService>(CorrelationIdService);
            services.AddSingleton<IAttributeEnricher>(AttributeEnricher);
            services.AddSingleton<IOpenTelemetryOptions>(options);
            _serviceProvider = services.BuildServiceProvider();

            var truncating = new TruncatingSpanProcessor(options, AttributeEnricher);
            var correlationProcessor = new CorrelationIdProcessor(_serviceProvider);

            var tracerBuilder = Sdk.CreateTracerProviderBuilder()
                .AddGmoInstrumentation(options)
                .AddProcessor(correlationProcessor)
                .AddProcessor(truncating);

            if (options.EnableSqlInstrumentation)
            {
                tracerBuilder.AddSqlClientInstrumentationWithEnrichment(
                    recordException: options.EnableSqlInstrumentationText,
                    enableTextEnrichment: options.EnableSqlInstrumentationText);
            }

            _tracerProvider = tracerBuilder.Build();
            _meterProvider = Sdk.CreateMeterProviderBuilder().AddGmoMetrics(options).Build();
            _customMetrics = new CustomMetrics();
#else
            var builder = Host.CreateApplicationBuilder();
            // The app logs through Serilog / log4net, not MEL; the default providers would add a
            // second console stream and the Windows EventLog provider.
            builder.Logging.ClearProviders();

            builder.Services.AddSingleton<ICorrelationIdService, CorrelationIdService>();
            builder.Services.AddSingleton<IAttributeEnricher, AttributeEnricher>();
            builder.Services.AddSingleton<IOpenTelemetryOptions>(options);
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(ActivitySource);

            builder.Services.AddGmoOpenTelemetry(
                options,
                additionalTracing: tracing =>
                {
                    if (options.EnableSqlInstrumentation)
                    {
                        tracing.AddSqlClientInstrumentationWithEnrichment(
                            recordException: options.EnableSqlInstrumentationText,
                            enableTextEnrichment: options.EnableSqlInstrumentationText);
                    }

                    tracing.AddNpgsqlInstrumentationWithEnrichment();
                },
                additionalMetrics: metrics => metrics.AddNpgsqlMetrics());

            if (!string.IsNullOrWhiteSpace(options.PostgresConnectionString))
            {
                PostgresDataSource = new NpgsqlDataSourceBuilder(options.PostgresConnectionString)
                    .ConfigureTracingWithEnrichment(enableTextEnrichment: options.EnableSqlInstrumentationText)
                    .Build();
            }

            _host = builder.Build();
            _host.Start();

            CorrelationIdService = _host.Services.GetRequiredService<ICorrelationIdService>();
            AttributeEnricher = _host.Services.GetRequiredService<IAttributeEnricher>();
#endif
        }

        public DemoOpenTelemetryOptions Options { get; }
        public ICorrelationIdService CorrelationIdService { get; }
        public IAttributeEnricher AttributeEnricher { get; }
        public ActivitySource ActivitySource { get; }
#if !NETFRAMEWORK
        public NpgsqlDataSource? PostgresDataSource { get; }
#endif

        public void Dispose()
        {
#if NETFRAMEWORK
            _tracerProvider.ForceFlush(10000);
            _meterProvider.ForceFlush(10000);
            _meterProvider.Dispose();
            _tracerProvider.Dispose();
            _customMetrics.Dispose();
            _serviceProvider.Dispose();
#else
            PostgresDataSource?.Dispose();
            _host.StopAsync().GetAwaiter().GetResult();
            _host.Dispose();
#endif
            ActivitySource.Dispose();
        }
    }
}
