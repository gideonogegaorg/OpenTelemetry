using System;
using System.Diagnostics;
using log4net;
using log4net.Core;

namespace GMO.OpenTelemetry.Log4Net
{
    /// <summary>
    /// Builds an <see cref="ActivityRunner"/> straight from a log4net <see cref="ILog"/>, so a call site
    /// binds its source and logger in one expression instead of naming both at every span.
    /// </summary>
    public static class Log4NetActivityRunnerExtensions
    {
        /// <summary>
        /// Binds an already-resolved <see cref="ILog"/>.
        /// </summary>
        /// <remarks>
        /// Correct only where the logger exists and is stable at bind time - typically a
        /// <c>static readonly ILog</c>. A property injected by a container is still null while the
        /// constructor runs, so use the <see cref="Func{ILog}"/> overload for those.
        /// </remarks>
        /// <param name="log">The log4net logger to bind.</param>
        /// <param name="activitySource">The source every span from the runner will come from.</param>
        /// <param name="logLevel">Level for activity start/end logging (default: Info).</param>
        /// <returns>A runner bound to <paramref name="activitySource"/> and <paramref name="log"/>.</returns>
        public static ActivityRunner ToActivityRunner(this ILog log, ActivitySource activitySource, Level? logLevel = null)
        {
            // Extension methods do not throw on a null receiver, so this has to be explicit.
#if NET6_0_OR_GREATER
            ArgumentNullException.ThrowIfNull(log);
#else
            if (log == null) throw new ArgumentNullException(nameof(log));
#endif

            return ActivityRunner.For(activitySource, log.ToActivityLogger(logLevel));
        }

        /// <summary>
        /// Binds an <see cref="ILog"/> resolved on every span. This is the overload for a
        /// container-injected logger property, an injected <c>Func&lt;ILog&gt;</c>, or a static
        /// <c>ILog Log()</c> accessor that re-resolves per call.
        /// </summary>
        /// <param name="log">Factory invoked once per span to obtain the logger.</param>
        /// <param name="activitySource">The source every span from the runner will come from.</param>
        /// <param name="logLevel">Level for activity start/end logging (default: Info).</param>
        /// <returns>A runner bound to <paramref name="activitySource"/> and <paramref name="log"/>.</returns>
        public static ActivityRunner ToActivityRunner(this Func<ILog> log, ActivitySource activitySource, Level? logLevel = null)
        {
#if NET6_0_OR_GREATER
            ArgumentNullException.ThrowIfNull(log);
#else
            if (log == null) throw new ArgumentNullException(nameof(log));
#endif

            return ActivityRunner.For(activitySource, () => log()?.ToActivityLogger(logLevel));
        }
    }
}
