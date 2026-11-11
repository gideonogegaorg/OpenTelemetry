using System.Collections.Generic;
using System.Diagnostics;

namespace GMO.OpenTelemetry
{
    /// <summary>
    /// Optional settings for <see cref="ActivityWrapper"/>, <see cref="RootActivityWrapper"/>, and
    /// <see cref="ActivityExecutor"/>.
    /// </summary>
    public sealed class ActivityOptions
    {
        /// <summary>
        /// Root activities only. When <c>false</c> (the default) an existing ambient root is reused and
        /// renamed rather than a new span being created. Set <c>true</c> for consumers, background jobs,
        /// and other entry points that must own their trace.
        /// </summary>
        public bool ForceNewDetachedRoot { get; set; }

        public IActivityLogger? Log { get; set; }

        /// <summary>Tags applied when the span starts.</summary>
        public Dictionary<string, object>? AdditionalData { get; set; }

        /// <summary>Remote parent to link, e.g. a context extracted from message headers.</summary>
        public ActivityContext? ParentContext { get; set; }

        public ActivityKind ActivityKind { get; set; } = ActivityKind.Internal;

        /// <summary>
        /// Extra links recorded when the span starts, e.g. the batch parent a producer span belongs to.
        /// A root span links its parent context automatically; these are added alongside.
        /// </summary>
        public IEnumerable<ActivityLink>? Links { get; set; }

        /// <summary>
        /// When <c>true</c> (the default) <c>Dispose</c> stamps an otherwise-unset span as <c>Ok</c>,
        /// including one whose body threw without <see cref="ActivityWrapper.RecordException"/>. Set
        /// <c>false</c> once the call site calls <see cref="ActivityWrapper.MarkSucceeded"/> itself.
        /// <see cref="ActivityExecutor"/> always clears it.
        /// </summary>
        public bool MarkSuccessOnDispose { get; set; } = true;
    }
}
