using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class ActivityRunnerTests : IDisposable
    {
        private readonly ActivitySource _source;
        private readonly ActivityListener _listener;

        public ActivityRunnerTests()
        {
            _source = new ActivitySource("Test.ActivityRunner");
            _listener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose()
        {
            _listener.Dispose();
            _source.Dispose();
            GC.SuppressFinalize(this);
        }

        /// <summary>Counts how often the bound factory is asked for a logger.</summary>
        private sealed class CountingLogger : IActivityLogger
        {
            public int Starts;
            public int Ends;
            public int Exceptions;
            public string? LastActivity;

            public void LogActivityStart(string activityName) { Starts++; LastActivity = activityName; }
            public void LogActivityEnd(string activityName, string status) => Ends++;
            public void LogException(string activityName, Exception ex, string? message = null) => Exceptions++;
        }

        #region Binding

        [Fact]
        public void For_NullActivitySource_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => ActivityRunner.For(null!));
            Assert.Throws<ArgumentNullException>(() => ActivityRunner.For(null!, (IActivityLogger?)null));
            Assert.Throws<ArgumentNullException>(() => ActivityRunner.For(null!, () => (IActivityLogger?)null));
        }

        [Fact]
        public void For_NullLoggerFactory_Throws() =>
            Assert.Throws<ArgumentNullException>(() => ActivityRunner.For(_source, (Func<IActivityLogger?>)null!));

        [Fact]
        public void For_NullLogger_IsAllowed()
        {
            var runner = ActivityRunner.For(_source, (IActivityLogger?)null);
            using var span = runner.Begin("op");
            Assert.NotNull(span.Activity);
        }

        [Fact]
        public void ActivitySource_ExposesBoundSource() =>
            Assert.Same(_source, ActivityRunner.For(_source).ActivitySource);

        #endregion

        #region Logger resolution

        [Fact]
        public void For_LoggerFactory_IsNotInvokedAtConstruction()
        {
            var calls = 0;
            ActivityRunner.For(_source, () => { calls++; return null; });

            // A static field initializer binds before the container is ready; resolving here would
            // throw inside the type initializer.
            Assert.Equal(0, calls);
        }

        [Fact]
        public void Begin_LoggerFactory_IsInvokedPerCall_NotCached()
        {
            var calls = 0;
            var runner = ActivityRunner.For(_source, () => { calls++; return null; });

            using (runner.Begin("one")) { }
            using (runner.Begin("two")) { }

            // Memoizing would turn a per-call container resolve into a one-shot.
            Assert.Equal(2, calls);
        }

        [Fact]
        public void Begin_DeferredLoggerAssignedAfterBinding_IsUsed()
        {
            // The container-injected-property shape: null at bind time, set afterwards.
            IActivityLogger? injected = null;
            var runner = ActivityRunner.For(_source, () => injected);

            var log = new CountingLogger();
            injected = log;

            using (runner.Begin("op")) { }

            Assert.Equal(1, log.Starts);
            Assert.Equal("op", log.LastActivity);
        }

        [Fact]
        public void Begin_UsesBoundLogger()
        {
            var log = new CountingLogger();
            using (ActivityRunner.For(_source, log).Begin("op")) { }

            Assert.Equal(1, log.Starts);
            Assert.Equal(1, log.Ends);
        }

        #endregion

        #region Merge

        [Fact]
        public void Begin_WithOverrides_DoesNotMutateCallerOptions()
        {
            var runner = ActivityRunner.For(_source, new CountingLogger());
            var overrides = new ActivityOptions { AdditionalData = new Dictionary<string, object> { ["k"] = "v" } };

            using (runner.Begin("op", overrides)) { }

            // ActivityOptions is mutable; the runner must never write the bound logger into it.
            Assert.Null(overrides.Log);
        }

        [Fact]
        public void Begin_CalledTwiceWithSameOverrides_ProducesIndependentOptions()
        {
            var runner = ActivityRunner.For(_source, new CountingLogger());
            var overrides = new ActivityOptions();

            using (runner.Begin("one", overrides)) { }
            using (runner.Begin("two", overrides)) { }

            Assert.Null(overrides.Log);
        }

        [Fact]
        public void Begin_OverrideLog_TakesPrecedenceOverBoundLogger()
        {
            var bound = new CountingLogger();
            var explicitLog = new CountingLogger();

            using (ActivityRunner.For(_source, bound).Begin("op", new ActivityOptions { Log = explicitLog })) { }

            Assert.Equal(0, bound.Starts);
            Assert.Equal(1, explicitLog.Starts);
        }

        [Fact]
        public void Begin_OverrideWithNullLog_FallsBackToBoundLogger()
        {
            var bound = new CountingLogger();
            using (ActivityRunner.For(_source, bound).Begin("op", new ActivityOptions())) { }

            Assert.Equal(1, bound.Starts);
        }

        [Fact]
        public void Begin_WithOverrides_AppliesAdditionalData()
        {
            var overrides = new ActivityOptions { AdditionalData = new Dictionary<string, object> { ["user.id"] = 7 } };
            using var span = ActivityRunner.For(_source).Begin("op", overrides);

            // Render passes primitives through unchanged, so this stays a boxed int.
            Assert.Equal(7, span.Activity!.GetTagItem("user.id"));
        }

        [Fact]
        public void Begin_WithActivityKind_AppliesIt()
        {
            using var span = ActivityRunner.For(_source)
                .Begin("op", new ActivityOptions { ActivityKind = ActivityKind.Producer });

            Assert.Equal(ActivityKind.Producer, span.Activity!.Kind);
        }

        [Fact]
        public void Begin_WithLinks_AppliesThem()
        {
            using var other = _source.StartActivity("linked")!;
            var link = new ActivityLink(other.Context);

            using var span = ActivityRunner.For(_source)
                .Begin("op", new ActivityOptions { Links = new[] { link } });

            Assert.Contains(span.Activity!.Links, l => l.Context == other.Context);
        }

        /// <summary>
        /// ActivityRunner.Merge copies ActivityOptions property by property, so a property added there
        /// and not to Merge is dropped silently - the span just loses the setting. This invokes Merge
        /// and compares every property, so a name list cannot be appended to re-green it: a new
        /// property has to be given a non-default value below AND carried by Merge.
        /// </summary>
        [Fact]
        public void Merge_CarriesEveryActivityOptionsProperty()
        {
            using var linked = _source.StartActivity("linked")!;
            var overrides = new ActivityOptions
            {
                ForceNewDetachedRoot = true,
                Log = new CountingLogger(),
                AdditionalData = new Dictionary<string, object> { ["k"] = "v" },
                ParentContext = linked.Context,
                ActivityKind = ActivityKind.Consumer,
                MarkSuccessOnDispose = false,
                Links = new[] { new ActivityLink(linked.Context) },
            };

            var settable = typeof(ActivityOptions)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite)
                .ToArray();

            // Every property must actually differ from a fresh instance, or the round-trip below would
            // pass vacuously for anything left at its default.
            var pristine = new ActivityOptions();
            foreach (var p in settable)
            {
                Assert.False(Equals(p.GetValue(overrides), p.GetValue(pristine)),
                    $"{p.Name} is still at its default in this test, so Merge dropping it would go unnoticed");
            }

            var merged = InvokeMerge(ActivityRunner.For(_source), overrides);

            foreach (var p in settable)
            {
                Assert.True(Equals(p.GetValue(overrides), p.GetValue(merged)),
                    $"ActivityRunner.Merge does not carry {p.Name}");
            }
        }

        private static ActivityOptions InvokeMerge(ActivityRunner runner, ActivityOptions overrides)
        {
            var merge = typeof(ActivityRunner).GetMethod("Merge",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            return (ActivityOptions)merge.Invoke(runner, new object?[] { overrides })!;
        }

        #endregion

        #region WithDefaults

        [Fact]
        public void WithDefaults_MarkSuccessOnDisposeFalse_LeavesStatusUnset()
        {
            // The reason this exists: a site moved off ActivitySource.StartActivity must keep ending
            // Unset, or a failed operation starts reporting success.
            var runner = ActivityRunner.For(_source)
                .WithDefaults(new ActivityOptions { MarkSuccessOnDispose = false });
            Activity? captured;

            using (var span = runner.Begin("op")) captured = span.Activity;

            Assert.Equal(ActivityStatusCode.Unset, captured!.Status);
        }

        [Fact]
        public void WithDefaults_DoesNotMutateTheOriginalRunner()
        {
            var runner = ActivityRunner.For(_source);
            _ = runner.WithDefaults(new ActivityOptions { MarkSuccessOnDispose = false });
            Activity? captured;

            using (var span = runner.Begin("op")) captured = span.Activity;

            Assert.Equal(ActivityStatusCode.Ok, captured!.Status);
        }

        [Fact]
        public void WithDefaults_KeepsTheBoundLogger()
        {
            var log = new CountingLogger();
            var runner = ActivityRunner.For(_source, log).WithDefaults(new ActivityOptions { ActivityKind = ActivityKind.Client });

            using (runner.Begin("op")) { }

            Assert.Equal(1, log.Starts);
        }

        [Fact]
        public void WithDefaults_LaterMutationOfTheOptionsObjectHasNoEffect()
        {
            var defaults = new ActivityOptions { MarkSuccessOnDispose = false };
            var runner = ActivityRunner.For(_source).WithDefaults(defaults);
            defaults.MarkSuccessOnDispose = true;
            Activity? captured;

            using (var span = runner.Begin("op")) captured = span.Activity;

            Assert.Equal(ActivityStatusCode.Unset, captured!.Status);
        }

        [Fact]
        public void WithDefaults_ExplicitOverridesReplaceThemWholesale()
        {
            var runner = ActivityRunner.For(_source)
                .WithDefaults(new ActivityOptions { MarkSuccessOnDispose = false });
            Activity? captured;

            // Documented contract: overrides replace the defaults, so MarkSuccessOnDispose returns to
            // its own default of true here rather than layering on top of the binding.
            using (var span = runner.Begin("op", new ActivityOptions { ActivityKind = ActivityKind.Client }))
                captured = span.Activity;

            Assert.Equal(ActivityKind.Client, captured!.Kind);
            Assert.Equal(ActivityStatusCode.Ok, captured.Status);
        }

        [Fact]
        public void WithDefaults_Null_ClearsThem()
        {
            var runner = ActivityRunner.For(_source)
                .WithDefaults(new ActivityOptions { MarkSuccessOnDispose = false })
                .WithDefaults(null);
            Activity? captured;

            using (var span = runner.Begin("op")) captured = span.Activity;

            Assert.Equal(ActivityStatusCode.Ok, captured!.Status);
        }

        [Fact]
        public void WithDefaults_AppliesToExecuteAndBeginRoot()
        {
            var runner = ActivityRunner.For(_source)
                .WithDefaults(new ActivityOptions { ActivityKind = ActivityKind.Consumer });
            Activity? fromExecute = null;

            runner.Execute("op", w => fromExecute = w.Activity);
            using var root = runner.BeginRoot("root");

            Assert.Equal(ActivityKind.Consumer, fromExecute!.Kind);
            Assert.Equal(ActivityKind.Consumer, root.Activity!.Kind);
        }

        [Fact]
        public void WithDefaults_LaterMutationOfAdditionalDataHasNoEffect()
        {
            var data = new Dictionary<string, object> { ["bound"] = "yes" };
            var runner = ActivityRunner.For(_source).WithDefaults(new ActivityOptions { AdditionalData = data });
            data["leaked"] = "yes";
            Activity? captured;

            using (var span = runner.Begin("op")) captured = span.Activity;

            Assert.NotNull(captured!.GetTagItem("bound"));
            Assert.Null(captured.GetTagItem("leaked"));
        }

        [Fact]
        public void WithDefaults_LaterMutationOfLinksHasNoEffect()
        {
            using var bound = _source.StartActivity("bound")!;
            using var added = _source.StartActivity("added")!;
            var links = new List<ActivityLink> { new ActivityLink(bound.Context) };
            var runner = ActivityRunner.For(_source).WithDefaults(new ActivityOptions { Links = links });
            links.Add(new ActivityLink(added.Context));

            using var span = runner.Begin("op");

            // Count, not identity: Activity.Context is default under the hierarchical id format, so any
            // two contexts compare equal. net48 starts hierarchical and flips to W3C only once some
            // other test class builds a TracerProvider, so an identity assertion here is a race.
            Assert.Single(span.Activity!.Links);
        }

        /// <summary>
        /// A lazy Links query bound as a default would otherwise be re-enumerated for every span the
        /// runner ever starts. Snapshot materialises it once, at bind time.
        /// </summary>
        [Fact]
        public void WithDefaults_LazyLinksAreEnumeratedOnceAtBindTime()
        {
            using var linked = _source.StartActivity("linked")!;
            var enumerations = 0;

            IEnumerable<ActivityLink> Lazy()
            {
                enumerations++;
                yield return new ActivityLink(linked.Context);
            }

            var runner = ActivityRunner.For(_source).WithDefaults(new ActivityOptions { Links = Lazy() });

            using (runner.Begin("one")) { }
            using (runner.Begin("two")) { }

            Assert.Equal(1, enumerations);
        }

        #endregion

        #region Dispose semantics

        [Fact]
        public void Begin_OnDispose_StampsOkByDefault()
        {
            var runner = ActivityRunner.For(_source);
            Activity? captured;

            using (var span = runner.Begin("op")) captured = span.Activity;

            Assert.Equal(ActivityStatusCode.Ok, captured!.Status);
        }

        /// <summary>
        /// Deliberate, and the reason Begin is a safe mechanical substitute for a hand-rolled wrapper:
        /// it reproduces that wrapper's behaviour exactly, including reporting a throwing body as Ok.
        /// Execute does not - it records the exception and never marks success.
        /// </summary>
        [Fact]
        public void Begin_WhenBodyThrows_StillStampsOk_DocumentedLimitation()
        {
            var runner = ActivityRunner.For(_source);
            Activity? captured = null;

            // Cast to Action: a lambda whose body ends in throw is convertible to any delegate, and
            // would otherwise bind to the obsolete Assert.Throws(Func<Task>) overload.
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var span = runner.Begin("op");
                captured = span.Activity;
                throw new InvalidOperationException("boom");
            }));

            Assert.Equal(ActivityStatusCode.Ok, captured!.Status);
        }

        [Fact]
        public void Begin_WithMarkSuccessOnDisposeFalse_LeavesStatusUnset()
        {
            var runner = ActivityRunner.For(_source);
            Activity? captured;

            using (var span = runner.Begin("op", new ActivityOptions { MarkSuccessOnDispose = false }))
                captured = span.Activity;

            Assert.Equal(ActivityStatusCode.Unset, captured!.Status);
        }

        [Fact]
        public void BeginRoot_ReturnsRootActivityWrapper()
        {
            using var span = ActivityRunner.For(_source).BeginRoot("root");
            Assert.NotNull(span.Root);
        }

        [Fact]
        public void BeginRoot_ForceNewDetachedRoot_DetachesFromAmbientParent()
        {
            using var ambient = _source.StartActivity("ambient")!;
            using var span = ActivityRunner.For(_source)
                .BeginRoot("root", new ActivityOptions { ForceNewDetachedRoot = true });

            // Activity.Id is unique under both W3C and hierarchical formats; TraceId is all-zeros
            // whenever Activity.DefaultIdFormat is Hierarchical, which is the net48 default and the
            // net6 test host's. Production hosts get W3C because the TracerProvider forces it, but
            // these tests never build one - so asserting on TraceId compares two zeros and passes or
            // fails by accident.
            Assert.Null(span.Activity!.Parent);
            Assert.NotEqual(ambient.Id, span.Activity.Id);
        }

        #endregion

        #region Delegation to ActivityExecutor

        [Fact]
        public async Task ExecuteAsync_ReturnsResultAndSetsOk()
        {
            Activity? captured = null;
            var result = await ActivityRunner.For(_source).ExecuteAsync("op", w =>
            {
                captured = w.Activity;
                return Task.FromResult(42);
            });

            Assert.Equal(42, result);
            Assert.Equal(ActivityStatusCode.Ok, captured!.Status);
        }

        [Fact]
        public async Task ExecuteAsync_Void_SetsOk()
        {
            Activity? captured = null;
            await ActivityRunner.For(_source).ExecuteAsync("op", w =>
            {
                captured = w.Activity;
                return Task.CompletedTask;
            });

            Assert.Equal(ActivityStatusCode.Ok, captured!.Status);
        }

        [Fact]
        public void Execute_ReturnsResultAndSetsOk()
        {
            Activity? captured = null;
            var result = ActivityRunner.For(_source).Execute("op", w => { captured = w.Activity; return 42; });

            Assert.Equal(42, result);
            Assert.Equal(ActivityStatusCode.Ok, captured!.Status);
        }

        [Fact]
        public void Execute_Void_SetsOk()
        {
            Activity? captured = null;
            ActivityRunner.For(_source).Execute("op", w => { captured = w.Activity; });

            Assert.Equal(ActivityStatusCode.Ok, captured!.Status);
        }

        [Fact]
        public async Task ExecuteRootAsync_ReturnsResult() =>
            Assert.Equal(7, await ActivityRunner.For(_source).ExecuteRootAsync("root", _ => Task.FromResult(7)));

        [Fact]
        public async Task ExecuteRootAsync_Void_SetsOk()
        {
            Activity? captured = null;
            await ActivityRunner.For(_source).ExecuteRootAsync("root", w =>
            {
                captured = w.Activity;
                return Task.CompletedTask;
            });

            Assert.Equal(ActivityStatusCode.Ok, captured!.Status);
        }

        [Fact]
        public void ExecuteRoot_ReturnsResult() =>
            Assert.Equal(7, ActivityRunner.For(_source).ExecuteRoot("root", _ => 7));

        [Fact]
        public void ExecuteRoot_Void_SetsOk()
        {
            Activity? captured = null;
            ActivityRunner.For(_source).ExecuteRoot("root", w => { captured = w.Activity; });

            Assert.Equal(ActivityStatusCode.Ok, captured!.Status);
        }

        /// <summary>Proves the executor path clears MarkSuccessOnDispose, unlike <c>Begin</c>.</summary>
        [Fact]
        public void Execute_WhenActionThrows_RecordsExceptionAndRethrows()
        {
            var log = new CountingLogger();
            var runner = ActivityRunner.For(_source, log);
            Activity? captured = null;

            Assert.Throws<InvalidOperationException>(() =>
                runner.Execute("op", w =>
                {
                    captured = w.Activity;
                    throw new InvalidOperationException("boom");
                }));

            Assert.Equal(ActivityStatusCode.Error, captured!.Status);
            Assert.Equal(1, log.Exceptions);
        }

        [Fact]
        public void ExecuteAsync_NullAction_ThrowsEagerly()
        {
            var runner = ActivityRunner.For(_source);

            // Void-bodied lambdas, so a faulted Task would fail these rather than satisfy them. This is
            // what catches a future `async` modifier on the runner's async overloads.
            Assert.Throws<ArgumentNullException>(() => { _ = runner.ExecuteAsync<int>("op", null!); });
            Assert.Throws<ArgumentNullException>(() => { _ = runner.ExecuteAsync("op", (Func<ActivityWrapper, Task>)null!); });
            Assert.Throws<ArgumentNullException>(() => { _ = runner.ExecuteRootAsync<int>("root", null!); });
            Assert.Throws<ArgumentNullException>(() => { _ = runner.ExecuteRootAsync("root", (Func<RootActivityWrapper, Task>)null!); });
        }

        [Fact]
        public void Execute_NullAction_Throws()
        {
            var runner = ActivityRunner.For(_source);

            Assert.Throws<ArgumentNullException>(() => runner.Execute<int>("op", null!));
            Assert.Throws<ArgumentNullException>(() => runner.Execute("op", (Action<ActivityWrapper>)null!));
            Assert.Throws<ArgumentNullException>(() => runner.ExecuteRoot<int>("root", null!));
            Assert.Throws<ArgumentNullException>(() => runner.ExecuteRoot("root", (Action<RootActivityWrapper>)null!));
        }

        [Fact]
        public void Execute_NullAction_ThrowsBeforeResolvingLogger()
        {
            var calls = 0;
            var runner = ActivityRunner.For(_source, () => { calls++; return null; });

            Assert.Throws<ArgumentNullException>(() => runner.Execute("op", (Action<ActivityWrapper>)null!));

            // The factory can throw (a container resolve); the null check must not depend on it.
            Assert.Equal(0, calls);
        }

        [Fact]
        public async Task ExecuteAsync_UsesBoundLoggerAndAppliesOverrides()
        {
            var log = new CountingLogger();
            var overrides = new ActivityOptions { AdditionalData = new Dictionary<string, object> { ["step"] = "one" } };
            Activity? captured = null;

            await ActivityRunner.For(_source, log).ExecuteAsync("op", w =>
            {
                captured = w.Activity;
                return Task.CompletedTask;
            }, overrides);

            Assert.Equal(1, log.Starts);
            Assert.Equal("one", captured!.GetTagItem("step"));
            Assert.Null(overrides.Log);
        }

        [Fact]
        public async Task ExecuteRootAsync_DoesNotLeakAmbientActivityToCaller()
        {
            var before = Activity.Current;

            await ActivityRunner.For(_source).ExecuteRootAsync(
                "root",
                _ => Task.CompletedTask,
                new ActivityOptions { ForceNewDetachedRoot = true });

            Assert.Same(before, Activity.Current);
        }

        #endregion
    }
}
