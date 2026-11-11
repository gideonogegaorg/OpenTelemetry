using System;
using log4net;
using log4net.Core;

namespace GMO.OpenTelemetry.Log4Net
{
    /// <summary>
    /// Routes <see cref="TelemetryDiagnostics"/> reports - failed and rejected exports, timeouts, dropped
    /// items from the OpenTelemetry SDK, its exporters and this library - into log4net.
    /// </summary>
    public static class Log4NetTelemetryDiagnostics
    {
        /// <summary>
        /// Starts reporting telemetry export problems to <paramref name="log"/>. Each is marked with
        /// <see cref="TelemetryDiagnostics.MarkerProperty"/>, which <see cref="OtlpLog4NetAppender"/> skips,
        /// so it reaches every other appender. Safe to call more than once; the last logger wins.
        /// </summary>
        public static void StartTelemetryDiagnostics(this ILog log)
        {
            // Extension methods do not throw on a null receiver, so this has to be explicit.
#if NET6_0_OR_GREATER
            ArgumentNullException.ThrowIfNull(log);
#else
            if (log == null) throw new ArgumentNullException(nameof(log));
#endif

            TelemetryDiagnostics.WriteTo(problem => Write(log, problem));
        }

        internal static void Write(ILog log, TelemetryProblem problem)
        {
            var loggingEvent = new LoggingEvent(typeof(Log4NetTelemetryDiagnostics), log.Logger.Repository,
                log.Logger.Name, problem.IsError ? Level.Error : Level.Warn, problem.ToString(), null);
            loggingEvent.Properties[TelemetryDiagnostics.MarkerProperty] = true;
            loggingEvent.Properties["TelemetrySource"] = problem.Source;
            loggingEvent.Properties["TelemetryEvent"] = problem.EventName;
            loggingEvent.Properties["TelemetrySuppressed"] = problem.Suppressed;
            log.Logger.Log(loggingEvent);
        }
    }
}
