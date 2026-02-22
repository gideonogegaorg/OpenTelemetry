using System;

namespace GMO.OpenTelemetry
{
    /// <summary>
    /// Abstraction for logging activity lifecycle events.
    /// Allows ActivityWrapper to work with different logging frameworks (Serilog, log4net, etc.)
    /// </summary>
    public interface IActivityLogger
    {
        /// <summary>
        /// Logs when an activity starts
        /// </summary>
        /// <param name="activityName">Name of the activity</param>
        void LogActivityStart(string activityName);

        /// <summary>
        /// Logs when an activity ends
        /// </summary>
        /// <param name="activityName">Name of the activity</param>
        /// <param name="status">Status of the activity (Ok, Error, Unset)</param>
        void LogActivityEnd(string activityName, string status);

        /// <summary>
        /// Logs an exception that occurred during an activity
        /// </summary>
        /// <param name="activityName">Name of the activity</param>
        /// <param name="ex">The exception to log</param>
        void LogException(string activityName, Exception ex);
    }
}
