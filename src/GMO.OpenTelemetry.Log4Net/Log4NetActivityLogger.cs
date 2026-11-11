using log4net;
using log4net.Core;

namespace GMO.OpenTelemetry.Log4Net
{
    /// <summary>
    /// log4net adapter for IActivityLogger interface
    /// </summary>
    public class Log4NetActivityLogger : IActivityLogger
    {
        private readonly ILog _log;
        private readonly Level _logLevel;

        /// <summary>
        /// Creates a new Log4NetActivityLogger
        /// </summary>
        /// <param name="log">The log4net ILog instance</param>
        /// <param name="logLevel">The log level to use for activity logging (default: Info)</param>
        public Log4NetActivityLogger(ILog log, Level? logLevel = null)
        {
            _log = log ?? throw new System.ArgumentNullException(nameof(log));
            _logLevel = logLevel ?? Level.Info;
        }

        /// <summary>
        /// Logs when an activity starts
        /// </summary>
        public void LogActivityStart(string activityName)
        {
            try
            {
                if (_log.Logger.IsEnabledFor(_logLevel))
                {
                    _log.Logger.Log(typeof(Log4NetActivityLogger), _logLevel, $"Starting activity {activityName}", null);
                }
            }
            catch
            {
                // Suppress logging errors
            }
        }

        /// <summary>
        /// Logs when an activity ends
        /// </summary>
        public void LogActivityEnd(string activityName, string status)
        {
            try
            {
                if (_log.Logger.IsEnabledFor(_logLevel))
                {
                    _log.Logger.Log(typeof(Log4NetActivityLogger), _logLevel, $"Completed activity {activityName} with status {status}", null);
                }
            }
            catch
            {
                // Suppress logging errors
            }
        }

        /// <summary>
        /// Logs an exception that occurred during an activity
        /// </summary>
        public void LogException(string activityName, System.Exception ex, string? message = null)
        {
            try
            {
                if (_log.Logger.IsEnabledFor(Level.Error))
                {
                    _log.Logger.Log(typeof(Log4NetActivityLogger), Level.Error, message ?? $"Exception in activity {activityName}", ex);
                }
            }
            catch
            {
                // Suppress logging errors
            }
        }
    }

    /// <summary>
    /// Extension methods for log4net ILog to work with ActivityWrapper
    /// </summary>
    public static class Log4NetActivityExtensions
    {
        /// <summary>
        /// Converts a log4net ILog to an IActivityLogger
        /// </summary>
        /// <param name="log">The log4net ILog instance</param>
        /// <param name="logLevel">The log level to use for activity logging (default: Info)</param>
        /// <returns>An IActivityLogger adapter</returns>
        public static IActivityLogger ToActivityLogger(this ILog log, Level? logLevel = null)
        {
            return new Log4NetActivityLogger(log, logLevel);
        }
    }
}
