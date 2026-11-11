using System;
using System.Diagnostics;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class ActivityExtensionsTests : IDisposable
    {
        private readonly ActivitySource _source;
        private readonly ActivityListener _listener;

        public ActivityExtensionsTests()
        {
            _source = new ActivitySource("Test.ActivityExtensions");
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

        [Fact]
        public void GetRoot_ReturnsNull_WhenActivityIsNull()
        {
            Activity? activity = null;
            var root = activity.GetRoot();
            Assert.Null(root);
        }

        [Fact]
        public void GetRoot_ReturnsSelf_WhenNoParent()
        {
            using var activity = _source.StartActivity("Root");
            Assert.NotNull(activity);

            // Clear parent to ensure this is a root
            Activity.Current = null;
            using var standalone = _source.StartActivity("Standalone");
            Assert.NotNull(standalone);

            var root = standalone!.GetRoot();
            Assert.Same(standalone, root);
        }

        [Fact]
        public void GetRoot_ReturnsRoot_WithParentChain()
        {
            using var root = _source.StartActivity("Root");
            Assert.NotNull(root);

            using var child = _source.StartActivity("Child");
            Assert.NotNull(child);

            using var grandchild = _source.StartActivity("Grandchild");
            Assert.NotNull(grandchild);

            var foundRoot = grandchild!.GetRoot();
            Assert.Same(root, foundRoot);
        }

        [Fact]
        public void GetRoot_InvokesAction_OnRoot()
        {
            using var root = _source.StartActivity("Root");
            Assert.NotNull(root);

            using var child = _source.StartActivity("Child");
            Assert.NotNull(child);

            Activity? captured = null;
            child!.GetRoot(a => captured = a);
            Assert.Same(root, captured);
        }

        [Fact]
        public void GetRoot_DoesNotInfiniteLoop_WithDeepChain()
        {
            // Create a chain deeper than maxDepth (50)
            // Since Activity parent chains are set automatically, we create many nested activities
            var activities = new Activity?[60];
            activities[0] = _source.StartActivity("Level0");

            for (int i = 1; i < 60; i++)
            {
                activities[i] = _source.StartActivity($"Level{i}");
            }

            // GetRoot should terminate even with deep chains (returns at depth 50)
            var result = activities[59]!.GetRoot();
            Assert.NotNull(result);

            // Clean up in reverse order
            for (int i = 59; i >= 0; i--)
            {
                activities[i]?.Dispose();
            }
        }

        [Fact]
        public void GetRoot_StaticOverload_UsesActivityCurrent()
        {
            using var root = _source.StartActivity("Root");
            Assert.NotNull(root);

            using var child = _source.StartActivity("Child");
            Assert.NotNull(child);

            var foundRoot = ActivityExtensions.GetRoot();
            Assert.Same(root, foundRoot);
        }

    }
}
