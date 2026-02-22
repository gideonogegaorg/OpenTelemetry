using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using OpenTelemetry.Trace;

namespace GMO.OpenTelemetry
{
    /// <summary>
    /// Wrapper for creating and managing OpenTelemetry activities/spans for operations.
    /// Provides automatic disposal, status tracking, and exception recording.
    /// </summary>
    public class ActivityWrapper : IDisposable
    {
        protected string _operationName;
        protected ActivitySource _activitySource;
        protected IActivityLogger? _log;
        protected Activity? _activity;
        protected bool _forceNewDetachedRoot;
        protected int _spanDepth = 0;
        protected Dictionary<string, object> _additionalData;
        protected ActivityContext _parentContext;
        protected ActivityKind _activityKind;

        /// <summary>
        /// Gets the underlying Activity
        /// </summary>
        public Activity? Activity => _activity;

        /// <summary>
        /// Creates a new ActivityWrapper
        /// </summary>
        /// <param name="operationName">Name of the operation/span</param>
        /// <param name="activitySource">The ActivitySource to create activities from</param>
        /// <param name="log">Optional logger for debug logging</param>
        /// <param name="forceNewDetachedRoot">If true, creates a new root activity detached from current context</param>
        /// <param name="additionalData">Additional tags to set on the activity</param>
        /// <param name="parentContext">Optional parent context for distributed tracing</param>
        /// <param name="activityKind">The kind of activity to create (default: Internal)</param>
        public ActivityWrapper(
            string operationName,
            ActivitySource activitySource,
            IActivityLogger? log = null,
            bool forceNewDetachedRoot = false,
            Dictionary<string, object>? additionalData = null,
            ActivityContext? parentContext = null,
            ActivityKind activityKind = ActivityKind.Internal)
        {
            _operationName = operationName ?? throw new ArgumentNullException(nameof(operationName));
            _activitySource = activitySource ?? throw new ArgumentNullException(nameof(activitySource));
            _forceNewDetachedRoot = forceNewDetachedRoot;
            _log = log;
            _additionalData = additionalData ?? new Dictionary<string, object>();
            _parentContext = parentContext ?? default;
            _activityKind = activityKind;

            _spanDepth = (System.Diagnostics.Activity.Current?.GetTagItem("span.depth") as int? ?? 0) + 1;
            _activity = StartActivity();
            _activity?.SetTag("span.depth", _spanDepth);

            foreach (var kvp in _additionalData)
            {
                SetTag(kvp.Key, kvp.Value);
            }

            LogActivityStart();
        }

        /// <summary>
        /// Sets a tag on the activity, serializing complex objects to JSON
        /// </summary>
        public void SetTag(string key, object value)
        {
            if (_activity == null) return;

            if (value != null && !IsPrimitive(value))
            {
                _activity.SetTag(key, System.Text.Json.JsonSerializer.Serialize(value));
            }
            else
            {
                _activity.SetTag(key, value);
            }
        }

        /// <summary>
        /// Sets the status of the activity
        /// </summary>
        public void SetStatus(ActivityStatusCode code, string? description = null)
        {
            _activity?.SetStatus(code, description);
        }

        /// <summary>
        /// Records an exception on the activity and sets error status
        /// </summary>
        public void RecordException(Exception ex)
        {
            if (_activity == null || ex == null) return;

            _activity.SetStatus(ActivityStatusCode.Error, ex.Message);

            // Add exception details as tags (compatible approach)
            _activity.SetTag("exception.type", ex.GetType().FullName);
            _activity.SetTag("exception.message", ex.Message);
            if (!string.IsNullOrEmpty(ex.StackTrace))
            {
                _activity.SetTag("exception.stacktrace", ex.StackTrace);
            }

            // Also add as an event for better visibility
            var exceptionTags = new ActivityTagsCollection
            {
                { "exception.type", ex.GetType().FullName },
                { "exception.message", ex.Message }
            };
            _activity.AddEvent(new ActivityEvent("exception", tags: exceptionTags));
            _log?.LogException(_operationName, ex);
        }

        /// <summary>
        /// Adds an event to the activity
        /// </summary>
        public void AddEvent(string name, Dictionary<string, object>? attributes = null)
        {
            if (_activity == null) return;

            if (attributes != null)
            {
                var tags = new ActivityTagsCollection(
                    attributes.Select(kvp => new KeyValuePair<string, object?>(kvp.Key, kvp.Value)));
                _activity.AddEvent(new ActivityEvent(name, tags: tags));
            }
            else
            {
                _activity.AddEvent(new ActivityEvent(name));
            }
        }

        public virtual void Dispose()
        {
            if (_activity != null)
            {
                if (_activity.Status == ActivityStatusCode.Unset)
                {
                    _activity.SetStatus(ActivityStatusCode.Ok);
                }
            }

            LogActivityEnd();

            _activity?.Dispose();
        }

        protected virtual Activity? StartActivity()
        {
            return _activitySource.StartActivity(_operationName, _activityKind);
        }

        protected virtual void LogActivityStart()
        {
            if (_log == null) return;

            try
            {
                _log.LogActivityStart(_operationName);
            }
            catch
            {
                // Ignore logging errors
            }
        }

        protected virtual void LogActivityEnd()
        {
            if (_log == null) return;

            try
            {
                var status = _activity?.Status.ToString() ?? "Unknown";
                _log.LogActivityEnd(_operationName, status);
            }
            catch
            {
                // Ignore logging errors
            }
        }

        private static bool IsPrimitive(object? value)
        {
            if (value == null) return true;

            var type = value.GetType();
            return type.IsPrimitive ||
                   type == typeof(string) ||
                   type == typeof(decimal) ||
                   type == typeof(DateTime) ||
                   type == typeof(DateTimeOffset) ||
                   type == typeof(TimeSpan) ||
                   type == typeof(Guid) ||
                   (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>) &&
                    IsPrimitive(Activator.CreateInstance(type.GetGenericArguments()[0])));
        }
    }
}
