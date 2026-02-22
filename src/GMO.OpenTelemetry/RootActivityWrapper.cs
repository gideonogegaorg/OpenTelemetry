using System;
using System.Collections.Generic;
using System.Diagnostics;
using OpenTelemetry.Trace;

namespace GMO.OpenTelemetry
{
    /// <summary>
    /// Used when operations/activities are nested in a generic root to force child activity into its own root
    /// or appropriately set root operation properties. Ideal for background tasks and message processing.
    /// </summary>
    public class RootActivityWrapper : ActivityWrapper
    {
        private Activity? _parent;
        private bool _isCreated = false;

        /// <summary>
        /// Gets the root activity
        /// </summary>
        public Activity? Root { get; private set; }

        /// <summary>
        /// Creates a new RootActivityWrapper
        /// </summary>
        /// <param name="rootOperationName">Name of the root operation</param>
        /// <param name="activitySource">The ActivitySource to create activities from</param>
        /// <param name="forceNewDetachedRoot">If true, creates a new root activity detached from current context</param>
        /// <param name="log">Optional logger for debug logging</param>
        /// <param name="additionalData">Additional tags to set on the activity</param>
        /// <param name="parentContext">Optional parent context for distributed tracing</param>
        /// <param name="activityKind">The kind of activity to create (default: Internal)</param>
        public RootActivityWrapper(
            string rootOperationName,
            ActivitySource activitySource,
            bool forceNewDetachedRoot = false,
            IActivityLogger? log = null,
            Dictionary<string, object>? additionalData = null,
            ActivityContext? parentContext = null,
            ActivityKind activityKind = ActivityKind.Internal)
            : base(rootOperationName, activitySource, log, forceNewDetachedRoot, additionalData, parentContext, activityKind)
        {
        }

        protected override Activity? StartActivity()
        {
            _parent = Activity.Current;

            Func<Activity?> create = () =>
            {
                _isCreated = true;

                var links = new List<ActivityLink>();

                // Remote trace - link to parent context if provided
                if (_parentContext != default)
                    links.Add(new ActivityLink(_parentContext));

                // Local parent trace - link to existing parent if different from parent context
                if (_parent != null && _parent.Context != _parentContext)
                    links.Add(new ActivityLink(_parent.Context));

                return _activitySource.StartActivity(
                    _operationName,
                    _activityKind,
                    parentContext: _parentContext,
                    tags: null,
                    links: links.Count > 0 ? links : null
                );
            };

            // If need a detached root, then create a new one
            if (_forceNewDetachedRoot)
            {
                // Force creating new Activity without parent by clearing Activity.Current
                // See: https://github.com/open-telemetry/opentelemetry-dotnet/issues/984
                Activity.Current = null;
                Root = create();
            }
            else // Rename the existing root to expected root operation name
            {
                Root = ActivityExtensions.GetRoot() ?? create();
                if (Root != null) Root.DisplayName = _operationName;
            }

            return Root;
        }

        public override void Dispose()
        {
            if (Root != null)
            {
                if (Root.Status == ActivityStatusCode.Unset)
                    Root.SetStatus(ActivityStatusCode.Ok);
            }

            LogActivityEnd();

            if (Root != null)
            {
                if (_isCreated)
                    Root.Dispose();
            }

            // Restore parent activity context
            Activity.Current = _parent;
        }
    }
}
