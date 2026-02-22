using Microsoft.Extensions.Logging;

namespace GMO.OpenTelemetry
{
    /// <summary>
    /// Microsoft.Extensions.Logging adapter for IActivityLogger interface
    /// </summary>
    public class MicrosoftLoggingActivityLogger : IActivityLogger
    {
        private readonly ILogger _logger;
        private readonly LogLevel _logLevel;

        /// <summary>
        /// Creates a new MicrosoftLoggingActivityLogger
        /// </summary>
        /// <param name="logger">The Microsoft.Extensions.Logging.ILogger instance</param>
        /// <param name="logLevel">The log level to use for activity logging (default: Information)</param>
        public MicrosoftLoggingActivityLogger(ILogger logger, LogLevel logLevel = LogLevel.Information)
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
                _logger.Log(_logLevel, "Starting activity {ActivityName}", activityName);
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
                _logger.Log(_logLevel, "Completed activity {ActivityName} with status {Status}", activityName, status);
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
                _logger.LogError(ex, "Exception in activity {ActivityName}", activityName);
            }
            catch
            {
                // Suppress logging errors
            }
        }
    }

    /// <summary>
    /// Extension methods for Microsoft.Extensions.Logging.ILogger to work with ActivityWrapper
    /// </summary>
    public static class MicrosoftLoggingActivityExtensions
    {
        /// <summary>
        /// Converts a Microsoft.Extensions.Logging.ILogger to an IActivityLogger
        /// </summary>
        /// <param name="logger">The Microsoft.Extensions.Logging.ILogger instance</param>
        /// <param name="logLevel">The log level to use for activity logging (default: Information)</param>
        /// <returns>An IActivityLogger adapter</returns>
        public static IActivityLogger ToActivityLogger(this ILogger logger, LogLevel logLevel = LogLevel.Information)
        {
            return new MicrosoftLoggingActivityLogger(logger, logLevel);
        }
    }
}
