using System;
using Serilog;
using Serilog.Events;

namespace GMO.OpenTelemetry.Serilog
{
    /// <summary>
    /// Routes <see cref="TelemetryDiagnostics"/> reports into Serilog and takes over Serilog's own
    /// <c>SelfLog</c>, the only place the OTLP sink reports a failed export.
    /// </summary>
    internal static class SerilogTelemetryDiagnostics
    {
        /// <summary>The logger reports go to. Resolved per report, so it follows <c>Log.Logger</c>.</summary>
        internal static Func<ILogger> Target { get; set; } = () => Log.Logger;

        internal static void Attach()
        {
            TelemetryDiagnostics.WriteTo(Write);
            global::Serilog.Debugging.SelfLog.Enable(TelemetryDiagnostics.ReportSelfLog);
        }

        internal static void Write(TelemetryProblem problem) =>
            Target()
                .ForContext(TelemetryDiagnostics.MarkerProperty, true)
                .Write(problem.IsError ? LogEventLevel.Error : LogEventLevel.Warning, TelemetryDiagnostics.MessageTemplate,
                    problem.Source, problem.EventName, problem.Detail, problem.Suppressed);
    }
}
