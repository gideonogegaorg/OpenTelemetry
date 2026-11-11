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
        protected IEnumerable<ActivityLink>? _links;

        private Activity? _suppressedParent;
        private bool _parentSuppressed;

        /// <summary>
        /// Gets the underlying Activity
        /// </summary>
        public Activity? Activity => _activity;

        /// <summary>Backs <see cref="ActivityOptions.MarkSuccessOnDispose"/>. Settable in-assembly so
        /// <see cref="ActivityExecutor"/> can clear it without mutating the caller's options object.</summary>
        protected internal bool MarkSuccessOnDispose { get; internal set; }

        /// <summary>
        /// Creates a new ActivityWrapper
        /// </summary>
        public ActivityWrapper(
            string operationName,
            ActivitySource activitySource,
            ActivityOptions? options = null)
        {
            _operationName = operationName ?? throw new ArgumentNullException(nameof(operationName));
            _activitySource = activitySource ?? throw new ArgumentNullException(nameof(activitySource));
            _forceNewDetachedRoot = options?.ForceNewDetachedRoot ?? false;
            _log = options?.Log;
            _additionalData = options?.AdditionalData ?? new Dictionary<string, object>();
            _parentContext = options?.ParentContext ?? default;
            _activityKind = options?.ActivityKind ?? ActivityKind.Internal;
            _links = options?.Links;
            MarkSuccessOnDispose = options?.MarkSuccessOnDispose ?? true;

            if (GetType() == typeof(ActivityWrapper))
                Initialize();
        }

        /// <summary>
        /// Initializes the activity. Called from the constructor by concrete classes
        /// to avoid virtual method calls during base construction.
        /// </summary>
        protected void Initialize()
        {
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
        public ActivityWrapper SetTag(string key, object? value)
        {
            if (_activity == null) return this;

            _activity.SetTag(key, Render(value));
            return this;
        }

        /// <summary>Anything not primitive becomes depth-limited JSON. Handed straight to the exporter
        /// instead, a collection or complex object arrives as its type name.</summary>
        private static object? Render(object? value) => ObjectRenderer.Render(value);

        /// <summary>
        /// Tags a collection as a count (<paramref name="name"/> + "Count") plus the collection itself.
        /// </summary>
        /// <remarks>
        /// Both values go through <see cref="SetTag(string, object?)"/> so serialization stays in one
        /// place: the collection is not primitive, so SetTag renders it as JSON and the element type
        /// no longer matters. Joining here instead would call ToString() on each element, which yields
        /// type names for anything that is not a primitive.
        /// <para>Handing the collection to <c>Activity.SetTag</c> directly would be worse still: a
        /// non-string tag value is invisible to <see cref="TruncatingSpanProcessor"/>, so it is never
        /// clamped to MaxAttributeValueLength and never shipped in full via AdditionalKeysToLog. A
        /// <c>List&lt;T&gt;</c> would export as its type name, an array as an OTLP array that silently
        /// skips both.</para>
        /// <para>The value is deliberately not truncated here - register <paramref name="name"/> in
        /// AdditionalKeysToLog to have the untruncated value emitted as a log.</para>
        /// <para>A primitive element type is de-duplicated first, because such a list is an identifier
        /// list: a repeated id carries no information, and leaving duplicates in inflates both the
        /// count and the payload. Element types that are not primitive are left alone - they default
        /// to reference equality, so Distinct would drop nothing while still costing a hash pass, and
        /// a caller tagging complex values may well depend on multiplicity.</para>
        /// </remarks>
        public ActivityWrapper SetListTags<T>(IEnumerable<T>? items, string name)
        {
            if (_activity == null || items == null) return this;

            if (IsPrimitiveType(typeof(T))) items = items.Distinct();

            var materialized = items as ICollection<T> ?? items.ToList();
            SetTag(name + "Count", materialized.Count);
            SetTag(name, materialized);
            return this;
        }

        /// <summary>
        /// Sets the status of the activity
        /// </summary>
        public ActivityWrapper SetStatus(ActivityStatusCode code, string? description = null)
        {
            _activity?.SetStatus(code, ObjectRenderer.Shorten(description));
            return this;
        }

        /// <summary>Sets <c>Ok</c> if nothing else set a status. Call it only where the outcome was
        /// actually observed.</summary>
        public ActivityWrapper MarkSucceeded()
        {
            if (_activity != null && _activity.Status == ActivityStatusCode.Unset)
                _activity.SetStatus(ActivityStatusCode.Ok);
            return this;
        }

        /// <summary>
        /// Records an exception on the activity and sets error status
        /// </summary>
        public ActivityWrapper RecordException(Exception? ex, string? message = null)
        {
            if (_activity == null) return this;

            if (ex == null)
            {
                if (!string.IsNullOrEmpty(message))
                    _activity.SetStatus(ActivityStatusCode.Error, ObjectRenderer.Shorten(message));
                return this;
            }

            _activity.SetStatus(ActivityStatusCode.Error, ObjectRenderer.Shorten(message ?? ex.Message));

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
                { "exception.type", (object?)ex.GetType().FullName },
                { "exception.message", ObjectRenderer.Shorten(ex.Message) }
            };
            _activity.AddEvent(new ActivityEvent("exception", tags: exceptionTags));

            try
            {
                _log?.LogException(_operationName, ex, message);
            }
            catch
            {
                // Ignore logging errors to avoid breaking the caller
            }

            return this;
        }

        /// <summary>
        /// The span's context, for propagating this span as the parent of work that starts elsewhere -
        /// a message being enqueued, or a batch item handed to another component.
        /// </summary>
        /// <remarks>
        /// Null when no listener is registered and no activity was created. Assign it to
        /// <see cref="ActivityOptions.ParentContext"/> at the far end.
        /// </remarks>
        public ActivityContext? Context => _activity?.Context;

        /// <summary>
        /// Ends the span now, while keeping this object usable. Only for a span whose measured duration
        /// must stop before the enclosing <c>using</c> does - reporting the work without the logging or
        /// cleanup that follows it.
        /// </summary>
        /// <remarks>
        /// This is not <see cref="Dispose()"/>: it neither marks success nor logs the activity end, and
        /// disposing afterwards is still safe.
        /// <para>
        /// Set the status before calling this. Stopping hands the span to the SDK, so a
        /// <see cref="MarkSucceeded"/> afterwards - including the one <see cref="Dispose()"/> performs by
        /// default - mutates an object the exporter has already taken and never reaches it. A span
        /// stopped early exports <c>Unset</c> even though the object reads <c>Ok</c>.
        /// </para>
        /// Prefer letting the <c>using</c> end the span.
        /// </remarks>
        public ActivityWrapper Stop()
        {
            _activity?.Stop();
            return this;
        }

        /// <summary>
        /// Adds a baggage item, which unlike a tag propagates to child spans and across process
        /// boundaries. Use sparingly - baggage travels on the wire with every downstream call.
        /// </summary>
        public ActivityWrapper AddBaggage(string key, string? value)
        {
            _activity?.AddBaggage(key, value);
            return this;
        }

        /// <summary>
        /// Links this span to another that is causally related but not its parent, e.g. the batch a
        /// producer span belongs to. Prefer <see cref="ActivityOptions.Links"/> when the link is known
        /// before the span starts.
        /// </summary>
        public ActivityWrapper AddLink(ActivityLink link)
        {
            _activity?.AddLink(link);
            return this;
        }

        /// <summary>
        /// Adds an already-constructed <see cref="ActivityEvent"/>.
        /// </summary>
        /// <remarks>
        /// The caller owns the tag values here, so they are used as given - unlike the
        /// <see cref="AddEvent(string, Dictionary{string, object})"/> overload, which runs each value
        /// through the same serialisation <see cref="SetTag"/> uses. Prefer that overload unless you
        /// already hold an <see cref="ActivityEvent"/>.
        /// </remarks>
        public ActivityWrapper AddEvent(ActivityEvent activityEvent)
        {
            _activity?.AddEvent(activityEvent);
            return this;
        }

        /// <summary>
        /// Adds an event to the activity, rendering each attribute value the way <see cref="SetTag"/> does.
        /// </summary>
        public ActivityWrapper AddEvent(string name, Dictionary<string, object>? attributes = null)
        {
            if (_activity == null) return this;

            if (attributes != null)
            {
                var tags = new ActivityTagsCollection(
                    attributes.Select(kvp => new KeyValuePair<string, object?>(kvp.Key, ObjectRenderer.Shorten(Render(kvp.Value)))));
                _activity.AddEvent(new ActivityEvent(name, tags: tags));
            }
            else
            {
                _activity.AddEvent(new ActivityEvent(name));
            }

            return this;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;

            // Default on for compatibility, not correctness: it cannot tell a success from a body that
            // threw without RecordException.
            if (MarkSuccessOnDispose) MarkSucceeded();

            LogActivityEnd();

            _activity?.Dispose();

            if (_parentSuppressed)
            {
                Activity.Current = _suppressedParent;
                _parentSuppressed = false;
                _suppressedParent = null;
            }
        }

        protected virtual Activity? StartActivity()
        {
            // Clearing Activity.Current is what detaches: StartActivity falls back to the ambient
            // activity whenever parentContext is default.
            if (_forceNewDetachedRoot)
            {
                _suppressedParent = Activity.Current;
                _parentSuppressed = true;
                Activity.Current = null;
            }

            return _activitySource.StartActivity(
                _operationName,
                _activityKind,
                parentContext: _parentContext,
                tags: null,
                links: _links);
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

        private static bool IsPrimitiveType(Type type)
        {
            return type.IsPrimitive ||
                   type == typeof(string) ||
                   type == typeof(decimal) ||
                   type == typeof(DateTime) ||
                   type == typeof(DateTimeOffset) ||
                   type == typeof(TimeSpan) ||
                   type == typeof(Guid) ||
                   (Nullable.GetUnderlyingType(type) is { } underlying && IsPrimitiveType(underlying));
        }
    }
}
