using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace GMO.OpenTelemetry
{
    /// <summary>
    /// Binds an <see cref="ActivitySource"/> and a logger once per class so call sites name neither.
    /// <para>
    /// <see cref="ActivityExecutor"/> takes both at every call, which makes
    /// <c>new ActivityOptions { Log = Log.ToActivityLogger() }</c> the most repeated expression in a
    /// large codebase even though both halves are fixed for the lifetime of the type. Bind once:
    /// </para>
    /// <code>
    /// static readonly ActivityRunner Runner = ActivityRunner.For(ActivitySource, () =&gt; Log().ToActivityLogger());
    /// // ...
    /// using (var span = Runner.Begin("SaveRate")) { ... }
    /// Runner.Execute("SaveRate", span =&gt; { ... });
    /// </code>
    /// </summary>
    /// <remarks>
    /// The logger is resolved through a factory on every span rather than captured once, because the two
    /// common host shapes cannot supply one at bind time: a property injected by a container is still
    /// null while the constructor runs, and a static <c>ILog Log()</c> accessor re-resolves from the
    /// container per call. The factory is deliberately <b>not</b> memoized - caching it would turn that
    /// per-call resolve into a one-shot. Supply a caching factory yourself if resolution is expensive.
    /// </remarks>
    public sealed class ActivityRunner
    {
        private readonly Func<IActivityLogger?> _log;
        private readonly ActivityOptions? _defaults;

        private ActivityRunner(ActivitySource activitySource, Func<IActivityLogger?> log, ActivityOptions? defaults)
        {
            ActivitySource = activitySource;
            _log = log;
            _defaults = defaults;
        }

        /// <summary>
        /// Returns a runner whose spans start from <paramref name="defaults"/> instead of the
        /// <see cref="ActivityOptions"/> defaults, so a setting shared by every span in a class is
        /// stated once at the binding rather than at each call.
        /// </summary>
        /// <remarks>
        /// The main use is <c>MarkSuccessOnDispose = false</c>. A span that was previously started with
        /// <c>ActivitySource.StartActivity</c> ends <c>Unset</c> unless something sets a status, so
        /// moving it to <see cref="Begin"/> - which stamps <c>Ok</c> on dispose - would make a failed
        /// operation report success. Binding that default keeps the original behaviour without
        /// repeating an options object at every site.
        /// <para>
        /// An <c>overrides</c> argument passed to <c>Begin</c> or <c>Execute</c> <b>replaces</b> these
        /// defaults rather than layering onto them: <see cref="ActivityOptions"/> has no way to tell
        /// "left at its default" from "deliberately set to the same value", so a per-property merge
        /// could not be well defined. A call site needing both must state both.
        /// </para>
        /// The options are copied - including <see cref="ActivityOptions.AdditionalData"/> and
        /// <see cref="ActivityOptions.Links"/> - so later mutation of <paramref name="defaults"/> cannot
        /// affect this runner.
        /// </remarks>
        public ActivityRunner WithDefaults(ActivityOptions? defaults) =>
            new ActivityRunner(ActivitySource, _log, defaults == null ? null : Snapshot(defaults));

        /// <summary>The bound source. Every span this runner starts comes from it.</summary>
        public ActivitySource ActivitySource { get; }

        /// <summary>Binds a source and an already-resolved logger.</summary>
        /// <remarks>
        /// Only correct where the logger exists and is stable at bind time. For a container-injected
        /// property or a per-call accessor use the <see cref="For(ActivitySource, Func{IActivityLogger})"/>
        /// overload. Note <c>For(source, null)</c> is ambiguous between the two overloads; write
        /// <c>For(source, (IActivityLogger?)null)</c> or use <see cref="For(ActivitySource)"/>.
        /// </remarks>
        public static ActivityRunner For(ActivitySource activitySource, IActivityLogger? log)
        {
            ThrowIfNull(activitySource, nameof(activitySource));

            return new ActivityRunner(activitySource, () => log, null);
        }

        /// <summary>
        /// Binds a source and a logger resolved on every span. This is the overload for a
        /// container-injected logger property, an injected <c>Func&lt;ILog&gt;</c>, or a static
        /// <c>ILog Log()</c> accessor.
        /// </summary>
        public static ActivityRunner For(ActivitySource activitySource, Func<IActivityLogger?> logFactory)
        {
            ThrowIfNull(activitySource, nameof(activitySource));
            ThrowIfNull(logFactory, nameof(logFactory));

            return new ActivityRunner(activitySource, logFactory, null);
        }

        /// <summary>Binds a source with no logger, for call sites that never had one.</summary>
        public static ActivityRunner For(ActivitySource activitySource) => For(activitySource, (IActivityLogger?)null);

        /// <summary>
        /// Starts a child span the caller owns and disposes - the bound replacement for
        /// <c>new ActivityWrapper(name, source, options)</c>.
        /// </summary>
        /// <remarks>
        /// Unlike <see cref="Execute(string, Action{ActivityWrapper}, ActivityOptions)"/> this leaves
        /// <see cref="ActivityOptions.MarkSuccessOnDispose"/> at its default, so a <c>using</c> block whose
        /// body throws without <see cref="ActivityWrapper.RecordException"/> is still stamped <c>Ok</c> on
        /// dispose. That matches a hand-rolled wrapper exactly, which is what makes it a safe mechanical
        /// substitution for one. Prefer <c>Execute</c> where the body can be a delegate; where it cannot,
        /// pass <c>MarkSuccessOnDispose = false</c> and call <see cref="ActivityWrapper.MarkSucceeded"/>.
        /// </remarks>
        public ActivityWrapper Begin(string operationName, ActivityOptions? overrides = null) =>
            new ActivityWrapper(operationName, ActivitySource, Merge(overrides));

        /// <summary>
        /// Starts a root span the caller owns and disposes - the bound replacement for
        /// <c>new RootActivityWrapper(name, source, options)</c>. Same dispose semantics as
        /// <see cref="Begin"/>.
        /// </summary>
        public RootActivityWrapper BeginRoot(string operationName, ActivityOptions? overrides = null) =>
            new RootActivityWrapper(operationName, ActivitySource, Merge(overrides));

        /// <summary>Runs <paramref name="action"/> inside a child span. See <see cref="ActivityExecutor"/>.</summary>
        public Task<T> ExecuteAsync<T>(string operationName, Func<ActivityWrapper, Task<T>> action, ActivityOptions? overrides = null)
        {
            // Checked here, and before Merge invokes the logger factory, because ActivityExecutor's
            // contract is that a null action throws synchronously rather than returning a faulted Task.
            // Declaring this method async would break that - ExecuteAsync_NullAction_ThrowsEagerly pins it.
            ActivityExecutor.ThrowIfActionNull(action);

            return ActivityExecutor.ExecuteAsync(operationName, ActivitySource, action, Merge(overrides));
        }

        /// <summary>Void-returning overload of <see cref="ExecuteAsync{T}"/>.</summary>
        public Task ExecuteAsync(string operationName, Func<ActivityWrapper, Task> action, ActivityOptions? overrides = null)
        {
            ActivityExecutor.ThrowIfActionNull(action);

            return ActivityExecutor.ExecuteAsync(operationName, ActivitySource, action, Merge(overrides));
        }

        /// <summary>Synchronous counterpart of <see cref="ExecuteAsync{T}"/>.</summary>
        public T Execute<T>(string operationName, Func<ActivityWrapper, T> action, ActivityOptions? overrides = null)
        {
            ActivityExecutor.ThrowIfActionNull(action);

            return ActivityExecutor.Execute(operationName, ActivitySource, action, Merge(overrides));
        }

        /// <summary>Void-returning overload of <see cref="Execute{T}"/>.</summary>
        public void Execute(string operationName, Action<ActivityWrapper> action, ActivityOptions? overrides = null)
        {
            ActivityExecutor.ThrowIfActionNull(action);

            ActivityExecutor.Execute(operationName, ActivitySource, action, Merge(overrides));
        }

        /// <summary>Runs <paramref name="action"/> inside a root span, for consumers and entry points.</summary>
        public Task<T> ExecuteRootAsync<T>(string operationName, Func<RootActivityWrapper, Task<T>> action, ActivityOptions? overrides = null)
        {
            ActivityExecutor.ThrowIfActionNull(action);

            return ActivityExecutor.ExecuteRootAsync(operationName, ActivitySource, action, Merge(overrides));
        }

        /// <summary>Void-returning overload of <see cref="ExecuteRootAsync{T}"/>.</summary>
        public Task ExecuteRootAsync(string operationName, Func<RootActivityWrapper, Task> action, ActivityOptions? overrides = null)
        {
            ActivityExecutor.ThrowIfActionNull(action);

            return ActivityExecutor.ExecuteRootAsync(operationName, ActivitySource, action, Merge(overrides));
        }

        /// <summary>Synchronous counterpart of <see cref="ExecuteRootAsync{T}"/>.</summary>
        public T ExecuteRoot<T>(string operationName, Func<RootActivityWrapper, T> action, ActivityOptions? overrides = null)
        {
            ActivityExecutor.ThrowIfActionNull(action);

            return ActivityExecutor.ExecuteRoot(operationName, ActivitySource, action, Merge(overrides));
        }

        /// <summary>Void-returning overload of <see cref="ExecuteRoot{T}"/>.</summary>
        public void ExecuteRoot(string operationName, Action<RootActivityWrapper> action, ActivityOptions? overrides = null)
        {
            ActivityExecutor.ThrowIfActionNull(action);

            ActivityExecutor.ExecuteRoot(operationName, ActivitySource, action, Merge(overrides));
        }

        /// <summary>
        /// Produces the <see cref="ActivityOptions"/> for one span: the caller's overrides with the bound
        /// logger filled in where they did not name one.
        /// </summary>
        /// <remarks>
        /// A fresh instance every time, and the caller's object is never stored or mutated -
        /// <see cref="ActivityOptions"/> is mutable, so sharing one across spans would let any call site
        /// alter every other. <see cref="ActivityOptions.AdditionalData"/> and
        /// <see cref="ActivityOptions.Links"/> are copied by reference here - a per-call object lives for
        /// one span. Defaults bound by <see cref="WithDefaults"/> outlive every call, so
        /// <see cref="Snapshot"/> materialises them instead.
        /// <para>
        /// Every property must be carried across. <c>Merge_CarriesEveryActivityOptionsProperty</c> fails
        /// when one is added to <see cref="ActivityOptions"/> and not here, because the omission is
        /// otherwise silent - the span simply loses the setting.
        /// </para>
        /// </remarks>
        private ActivityOptions Merge(ActivityOptions? overrides)
        {
            // An explicit overrides object replaces the bound defaults wholesale - see WithDefaults.
            var basis = overrides ?? _defaults;
            var log = basis?.Log ?? _log();

            return basis == null ? new ActivityOptions { Log = log } : Copy(basis, log);
        }

        /// <summary>
        /// Copies every <see cref="ActivityOptions"/> property, substituting <paramref name="log"/>.
        /// </summary>
        /// <remarks>
        /// A property added to <see cref="ActivityOptions"/> and not here is dropped silently - the span
        /// simply loses the setting. <c>Merge_CarriesEveryActivityOptionsProperty</c> fails when that
        /// happens.
        /// </remarks>
        private static ActivityOptions Copy(ActivityOptions from, IActivityLogger? log) =>
            new ActivityOptions
            {
                ForceNewDetachedRoot = from.ForceNewDetachedRoot,
                Log = log,
                AdditionalData = from.AdditionalData,
                ParentContext = from.ParentContext,
                ActivityKind = from.ActivityKind,
                MarkSuccessOnDispose = from.MarkSuccessOnDispose,
                Links = from.Links,
            };

        /// <summary><see cref="Copy"/> plus a materialised copy of the reference-typed settings.</summary>
        /// <remarks>
        /// A bound runner is typically a static field, so sharing the caller's dictionary would alias it
        /// across every span the runner ever starts - and the wrapper enumerates it per span, making a
        /// later write a concurrent-modification crash rather than just a leak. A lazy
        /// <see cref="ActivityOptions.Links"/> query would likewise be re-enumerated per span. Per-call
        /// overrides live for a single span and do not need this.
        /// </remarks>
        private static ActivityOptions Snapshot(ActivityOptions from)
        {
            var copy = Copy(from, from.Log);

            if (from.AdditionalData != null)
                copy.AdditionalData = new Dictionary<string, object>(from.AdditionalData);

            if (from.Links != null)
                copy.Links = new List<ActivityLink>(from.Links);

            return copy;
        }

        // Not ArgumentNullException.ThrowIfNull: its paramName is [CallerArgumentExpression], so passing
        // one explicitly reports "value" instead of the caller's argument name.
        private static void ThrowIfNull(object? value, string paramName)
        {
            if (value == null) throw new ArgumentNullException(paramName);
        }
    }
}
