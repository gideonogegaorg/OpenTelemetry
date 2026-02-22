using Serilog;
using Serilog.Events;

namespace GMO.OpenTelemetry.Serilog
{
    /// <summary>
    /// Serilog adapter for IActivityLogger interface
    /// </summary>
    public class SerilogActivityLogger : IActivityLogger
    {
        private readonly ILogger _logger;
        private readonly LogEventLevel _logLevel;

        /// <summary>
        /// Creates a new SerilogActivityLogger
        /// </summary>
        /// <param name="logger">The Serilog logger instance</param>
        /// <param name="logLevel">The log level to use for activity logging (default: Information)</param>
        public SerilogActivityLogger(ILogger logger, LogEventLevel logLevel = LogEventLevel.Information)
        {
            _logger = logger ?? throw new System.ArgumentNullException(nameof(logger));
            _logLevel = logLevel;
        }

        /// <summary>
        /// Logs when an activity starts
        /// </summary>
        public void LogActivityStart(string activityName)
        {
            try
            {
                _logger.Write(_logLevel, "Starting activity {ActivityName}", activityName);
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
                _logger.Write(_logLevel, "Completed activity {ActivityName} with status {Status}", activityName, status);
            }
            catch
            {
                // Suppress logging errors
            }
        }

        /// <summary>
        /// Logs an exception that occurred during an activity
        /// </summary>
        public void LogException(string activityName, System.Exception ex)
        {
            try
            {
                _logger.Write(LogEventLevel.Error, ex, "Exception in activity {ActivityName}", activityName);
            }
            catch
            {
                // Suppress logging errors
            }
        }
    }

    /// <summary>
    /// Extension methods for Serilog ILogger to work with ActivityWrapper
    /// </summary>
    public static class SerilogActivityExtensions
    {
        /// <summary>
        /// Converts a Serilog ILogger to an IActivityLogger
        /// </summary>
        /// <param name="logger">The Serilog logger</param>
        /// <param name="logLevel">The log level to use for activity logging (default: Information)</param>
        /// <returns>An IActivityLogger adapter</returns>
        public static IActivityLogger ToActivityLogger(this ILogger logger, LogEventLevel logLevel = LogEventLevel.Information)
        {
            return new SerilogActivityLogger(logger, logLevel);
        }
    }
}
