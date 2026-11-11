using System;
using System.IO;
using System.Threading.Tasks;
using GMO.OpenTelemetry.Serilog;
using GMO.OpenTelemetry.TestApps.Common;
using Serilog;

namespace TestApp.Serilog
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
        private const string ServiceName = "otel-testapp-serilog-" + Tfm;

        private static async Task<int> Main(string[] args)
        {
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
            var configuration = DemoOpenTelemetryOptions.BuildConfiguration();
            var options = DemoOpenTelemetryOptions.FromConfiguration(configuration, ServiceName);

            using var host = new TestAppHost(options);

            var loggerConfig = new LoggerConfiguration();
            loggerConfig.ReadSerilogConfigWithoutSinks(configuration);
            loggerConfig.WriteTo.Console();
            loggerConfig.AddOpenTelemetryWithEnricher(options, configuration, host.AttributeEnricher);
            Log.Logger = loggerConfig.CreateLogger();

            try
            {
                var activityLogger = Log.Logger.ToActivityLogger();
                var port = GetPort(args, 5080);
                return await TestAppHostRunner.RunAsync(
                        host, activityLogger, msg => Log.Information("{Message}", msg), port, "smoke-" + ServiceName)
                    .ConfigureAwait(false);
            }
            finally
            {
                Log.CloseAndFlush();
            }
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
