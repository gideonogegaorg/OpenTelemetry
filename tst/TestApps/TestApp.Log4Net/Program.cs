using System;
using System.IO;
using System.Threading.Tasks;
using GMO.OpenTelemetry;
using GMO.OpenTelemetry.Log4Net;
using GMO.OpenTelemetry.TestApps.Common;
using log4net;
using log4net.Config;
using log4net.Repository.Hierarchy;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

namespace TestApp.Log4Net
{
    internal static class Program
    {
#if NET10_0
        private const string Tfm = "net10";
#elif NET9_0
        private const string Tfm = "net9";
#elif NET8_0
        private const string Tfm = "net8";
#else
        private const string Tfm = "net48";
#endif
        private const string ServiceName = "otel-testapp-log4net-" + Tfm;

        private static async Task<int> Main(string[] args)
        {
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
            var configuration = DemoOpenTelemetryOptions.BuildConfiguration();
            var options = DemoOpenTelemetryOptions.FromConfiguration(configuration, ServiceName);

            using var host = new TestAppHost(options);

            using var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.AddConsole();
                logging.AddOpenTelemetry(otel =>
                {
                    // The host owns the logs exporter's resource; stamp service.name so log records
                    // are attributed to this app (not "unknown_service").
                    otel.SetResourceBuilder(ResourceBuilder.CreateDefault()
                        .AddService(ServiceName, serviceVersion: options.Version));
                    otel.IncludeFormattedMessage = true;
                    otel.IncludeScopes = true;
                    otel.AddOtlpExporter((exporter, processor) =>
                    {
                        processor.ApplyLogsBatching(options);

#if NETFRAMEWORK
                        // Framework HttpClient has no HTTP/2, so OTLP is HttpProtobuf-only.
                        var protocol = OtlpExportProtocol.HttpProtobuf;
#else
                        var protocol = options.Otlp?.Protocol ?? OtlpExportProtocol.Grpc;
#endif
                        exporter.Protocol = protocol;

                        Uri? baseEndpoint;
                        if (!string.IsNullOrWhiteSpace(options.LoggingEndpoint)
                            && Uri.TryCreate(options.LoggingEndpoint, UriKind.Absolute, out var uri))
                        {
                            baseEndpoint = uri;
                        }
                        else
                        {
                            baseEndpoint = options.GetOtlpEndpoint();
                        }

                        if (baseEndpoint != null)
                        {
                            exporter.Endpoint = protocol == OtlpExportProtocol.HttpProtobuf
                                ? OpenTelemetryExtensions.AppendOtlpSignalPath(baseEndpoint, "v1/logs")
                                : baseEndpoint;
                        }

                        var headers = options.GetOtlpHeaders();
                        if (!string.IsNullOrWhiteSpace(headers))
                            exporter.Headers = headers;

                        exporter.ApplyGzipCompression(options);
                    });
                });
            });

            var appender = new OtlpLog4NetAppender(loggerFactory, host.AttributeEnricher, options);
            // The console shows telemetry export problems, which the OTLP appender skips.
            var console = new log4net.Appender.ConsoleAppender { Layout = new log4net.Layout.PatternLayout("%date %-5level %message%newline") };
            BasicConfigurator.Configure(appender, console);
            var log = LogManager.GetLogger(typeof(Program));
            log.StartTelemetryDiagnostics();
            var activityLogger = log.ToActivityLogger();

            var port = GetPort(args, 5090);
            var exit = await TestAppHostRunner.RunAsync(
                    host, activityLogger, msg => log.Info(msg), port, "smoke-" + ServiceName)
                .ConfigureAwait(false);

            ((Hierarchy)LogManager.GetRepository()).Shutdown();
            return exit;
        }

        private static int GetPort(string[] args, int defaultPort)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--port" && int.TryParse(args[i + 1], out var port))
                    return port;
            }

            return defaultPort;
        }
    }
}
