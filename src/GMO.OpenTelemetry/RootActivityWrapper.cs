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
        private Activity? _parent = null;
        private bool _isCreated = false;

        /// <summary>
        /// Gets the root activity
        /// </summary>
        public Activity? Root { get; private set; }

        /// <summary>
        /// Creates a new RootActivityWrapper
        /// </summary>
        public RootActivityWrapper(
            string rootOperationName,
            ActivitySource activitySource,
            ActivityOptions? options = null)
            : base(rootOperationName, activitySource, options)
        {
            Initialize();
        }

        protected override Activity? StartActivity()
        {
            _parent = Activity.Current;

            if (_forceNewDetachedRoot)
            {
                // Force creating new Activity without parent by clearing Activity.Current
                // See: https://github.com/open-telemetry/opentelemetry-dotnet/issues/984
                Activity.Current = null;
                Root = CreateRoot();
            }
            else
            {
                Root = ActivityExtensions.GetRoot() ?? CreateRoot();
                AdoptRoot();
            }

            return Root;
        }

        private Activity? CreateRoot()
        {
            _isCreated = true;

            var links = BuildLinks();
            return _activitySource.StartActivity(
                _operationName,
                _activityKind,
                parentContext: _parentContext,
                tags: null,
                links: links
            );
        }

        private List<ActivityLink>? BuildLinks()
        {
            var links = new List<ActivityLink>();

            // Remote trace - link to parent context if provided
            if (_parentContext != default)
                links.Add(new ActivityLink(_parentContext));

            // Never link the local parent: a root is only created here when there is none or when
            // detaching, and on .NET Framework 4.8 that link makes a detached root inherit the parent's TraceId.

            // Caller-supplied links are additive - a root span already links its parent context.
            if (_links != null) links.AddRange(_links);

            return links.Count > 0 ? links : null;
        }

        /// <summary>Renames the ambient root this wrapper is taking over. Reusing one skips
        /// <see cref="CreateRoot"/>, where links are attached, so they are applied here instead - without
        /// this the caller's Links are silently dropped on the default (non-detached) path.</summary>
        private void AdoptRoot()
        {
            if (Root == null) return;

            Root.DisplayName = _operationName;

            if (_isCreated || _links == null) return;

            foreach (var link in _links) Root.AddLink(link);
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing) return;

            try
            {
                if (MarkSuccessOnDispose) MarkSucceeded();

                LogActivityEnd();

                if (Root != null && _isCreated)
                    Root.Dispose();
            }
            catch
            {
                // Suppress activity disposal errors to avoid breaking the caller
            }
            finally
            {
                // Always restore parent activity context
                Activity.Current = _parent;

                // Null out shared fields so base.Dispose is a safe no-op
                _activity = null;
                _log = null;
                base.Dispose(disposing);
            }
        }
    }
}
