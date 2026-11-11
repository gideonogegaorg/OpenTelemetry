using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace GMO.OpenTelemetry
{
    /// <summary>
    /// Runs work inside an <see cref="ActivityWrapper"/>/<see cref="RootActivityWrapper"/>, recording any
    /// exception on the span before rethrowing and marking <c>Ok</c> only when the delegate returns. It
    /// clears <see cref="ActivityOptions.MarkSuccessOnDispose"/>; a hand-rolled wrapper keeps that
    /// dispose-time stamp and so reports an unrecorded exception as a success.
    /// <para>
    /// Cancellation is not an error: <see cref="OperationCanceledException"/> adds a <c>cancelled</c>
    /// event and rethrows without setting <see cref="ActivityStatusCode.Error"/>.
    /// </para>
    /// <para>
    /// <see cref="ActivityOptions.ForceNewDetachedRoot"/> defaults to <c>false</c>, which reuses and
    /// renames the caller's ambient root instead of creating a span. Set it for entry points that must
    /// own their trace.
    /// </para>
    /// </summary>
    public static class ActivityExecutor
    {
        private const string CancelledEvent = "cancelled";

        /// <summary>Runs <paramref name="action"/> inside an <see cref="ActivityWrapper"/>.</summary>
        public static Task<T> ExecuteAsync<T>(
            string operationName,
            ActivitySource activitySource,
            Func<ActivityWrapper, Task<T>> action,
            ActivityOptions? options = null)
        {
            ThrowIfActionNull(action);

            return ExecuteCoreAsync(() => new ActivityWrapper(operationName, activitySource, options), action);
        }

        /// <summary>Void-returning overload of <see cref="ExecuteAsync{T}"/>.</summary>
        public static Task ExecuteAsync(
            string operationName,
            ActivitySource activitySource,
            Func<ActivityWrapper, Task> action,
            ActivityOptions? options = null)
        {
            ThrowIfActionNull(action);

            return ExecuteCoreAsync<ActivityWrapper, object?>(
                () => new ActivityWrapper(operationName, activitySource, options),
                async wrapper =>
                {
                    await action(wrapper).ConfigureAwait(false);
                    return null;
                });
        }

        /// <summary>Synchronous counterpart of <see cref="ExecuteAsync{T}"/>.</summary>
        public static T Execute<T>(
            string operationName,
            ActivitySource activitySource,
            Func<ActivityWrapper, T> action,
            ActivityOptions? options = null)
        {
            ThrowIfActionNull(action);

            return ExecuteCore(new ActivityWrapper(operationName, activitySource, options), action);
        }

        /// <summary>Void-returning overload of <see cref="Execute{T}"/>.</summary>
        public static void Execute(
            string operationName,
            ActivitySource activitySource,
            Action<ActivityWrapper> action,
            ActivityOptions? options = null)
        {
            ThrowIfActionNull(action);

            ExecuteCore<ActivityWrapper, object?>(
                new ActivityWrapper(operationName, activitySource, options),
                wrapper =>
                {
                    action(wrapper);
                    return null;
                });
        }

        /// <summary>Runs <paramref name="action"/> inside a <see cref="RootActivityWrapper"/>, for consumers
        /// and request entry points.</summary>
        public static Task<T> ExecuteRootAsync<T>(
            string operationName,
            ActivitySource activitySource,
            Func<RootActivityWrapper, Task<T>> action,
            ActivityOptions? options = null)
        {
            ThrowIfActionNull(action);

            return ExecuteCoreAsync(() => new RootActivityWrapper(operationName, activitySource, options), action);
        }

        /// <summary>Void-returning overload of <see cref="ExecuteRootAsync{T}"/>.</summary>
        public static Task ExecuteRootAsync(
            string operationName,
            ActivitySource activitySource,
            Func<RootActivityWrapper, Task> action,
            ActivityOptions? options = null)
        {
            ThrowIfActionNull(action);

            return ExecuteCoreAsync<RootActivityWrapper, object?>(
                () => new RootActivityWrapper(operationName, activitySource, options),
                async wrapper =>
                {
                    await action(wrapper).ConfigureAwait(false);
                    return null;
                });
        }

        /// <summary>Synchronous counterpart of <see cref="ExecuteRootAsync{T}"/>.</summary>
        public static T ExecuteRoot<T>(
            string operationName,
            ActivitySource activitySource,
            Func<RootActivityWrapper, T> action,
            ActivityOptions? options = null)
        {
            ThrowIfActionNull(action);

            return ExecuteCore(new RootActivityWrapper(operationName, activitySource, options), action);
        }

        /// <summary>Void-returning overload of <see cref="ExecuteRoot{T}"/>.</summary>
        public static void ExecuteRoot(
            string operationName,
            ActivitySource activitySource,
            Action<RootActivityWrapper> action,
            ActivityOptions? options = null)
        {
            ThrowIfActionNull(action);

            ExecuteCore<RootActivityWrapper, object?>(
                new RootActivityWrapper(operationName, activitySource, options),
                wrapper =>
                {
                    action(wrapper);
                    return null;
                });
        }

        // net48 has no ArgumentNullException.ThrowIfNull, hence the split.
        internal static void ThrowIfActionNull(object? action)
        {
#if NET
            ArgumentNullException.ThrowIfNull(action);
#else
            if (action == null) throw new ArgumentNullException(nameof(action));
#endif
        }

        /// <remarks>
        /// The wrapper must be built here rather than by the caller: starting an activity writes
        /// <see cref="Activity.Current"/> (an AsyncLocal), and only this async body runs in a
        /// copy-on-write ExecutionContext scope that keeps the write from leaking to the caller.
        /// </remarks>
        private static async Task<TResult> ExecuteCoreAsync<TWrapper, TResult>(
            Func<TWrapper> activityFactory,
            Func<TWrapper, Task<TResult>> action)
            where TWrapper : ActivityWrapper
        {
            using (var activity = activityFactory())
            {
                activity.MarkSuccessOnDispose = false;
                try
                {
                    var result = await action(activity).ConfigureAwait(false);
                    activity.MarkSucceeded();
                    return result;
                }
                catch (OperationCanceledException)
                {
                    activity.AddEvent(CancelledEvent);
                    throw;
                }
                catch (Exception ex)
                {
                    activity.RecordException(ex);
                    throw;
                }
            }
        }

        private static TResult ExecuteCore<TWrapper, TResult>(
            TWrapper activity,
            Func<TWrapper, TResult> action)
            where TWrapper : ActivityWrapper
        {
            using (activity)
            {
                activity.MarkSuccessOnDispose = false;
                try
                {
                    var result = action(activity);
                    activity.MarkSucceeded();
                    return result;
                }
                catch (OperationCanceledException)
                {
                    activity.AddEvent(CancelledEvent);
                    throw;
                }
                catch (Exception ex)
                {
                    activity.RecordException(ex);
                    throw;
                }
            }
        }
    }
}
