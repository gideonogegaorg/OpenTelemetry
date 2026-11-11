using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace GMO.OpenTelemetry.TestApps.Common
{
    public static class TestAppHostRunner
    {
        public static async Task<int> RunAsync(
            TestAppHost host,
            IActivityLogger activityLogger,
            Action<string> writeLog,
            int port,
            string smokeId)
        {
            using var listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:" + port + "/");
            listener.Start();
            Console.WriteLine("Listening on http://localhost:" + port + "/telemetry/run");

            var autoResult = await RunScenariosAsync(host, activityLogger, writeLog, smokeId).ConfigureAwait(false);
            Console.WriteLine("Auto-run success=" + autoResult.Success);

            var getTask = listener.GetContextAsync();
            var completed = await Task.WhenAny(getTask, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            if (completed == getTask)
            {
                var context = await getTask.ConfigureAwait(false);
                var header = context.Request.Headers["X-Correlation-ID"];
                var result = await RunScenariosAsync(host, activityLogger, writeLog, header).ConfigureAwait(false);
                var buffer = Encoding.UTF8.GetBytes("{\"success\":" + result.Success.ToString().ToLowerInvariant() + "}");
                context.Response.ContentType = "application/json";
                context.Response.OutputStream.Write(buffer, 0, buffer.Length);
                context.Response.Close();
            }

            listener.Stop();
            return autoResult.Success ? 0 : 1;
        }

        private static Task<ScenarioResult> RunScenariosAsync(
            TestAppHost host,
            IActivityLogger activityLogger,
            Action<string> writeLog,
            string? correlationHeader)
        {
            if (!string.IsNullOrWhiteSpace(correlationHeader))
                host.CorrelationIdService.SetCorrelationId(correlationHeader!);

            var runner = new TelemetryScenarioRunner(
                host.Options,
                host.ActivitySource,
                host.CorrelationIdService,
                host.AttributeEnricher,
                activityLogger,
                writeLog: writeLog,
                writeInfo: msg => Console.WriteLine(msg)
#if !NETFRAMEWORK
                , postgresDataSource: host.PostgresDataSource
#endif
                );

            return runner.RunAsync();
        }
    }
}
