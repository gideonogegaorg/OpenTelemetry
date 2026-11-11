using System;
using System.Diagnostics;
using System.Reflection;
using Moq;
using OpenTelemetry.Trace;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class RootActivityWrapperTests : IDisposable
    {
        private readonly ActivitySource _source;
        private readonly ActivityListener _listener;

        public RootActivityWrapperTests()
        {
            _source = new ActivitySource("Test.Root.Source");
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
        public void Constructor_CreatesRoot_WithName()
        {
            using var wrapper = new RootActivityWrapper("RootOp", _source);
            Assert.NotNull(wrapper.Root);
            Assert.Equal("RootOp", wrapper.Root?.DisplayName);
        }

        [Fact]
        public void Constructor_ForceNewDetachedRoot_ClearsParent()
        {
            using var parent = _source.StartActivity("Parent");
            Assert.NotNull(Activity.Current);

            using var wrapper = new RootActivityWrapper("RootOp", _source, new ActivityOptions { ForceNewDetachedRoot = true });

            Assert.NotNull(wrapper.Root);
            // A detached root has no parent and is a distinct activity from the original
            Assert.Null(wrapper.Root!.Parent);
            Assert.NotSame(parent, wrapper.Root);
            // Activity.Id is unique in both W3C and hierarchical formats;
            // TraceId is not reliable on .NET Framework 4.8 (hierarchical format uses zeros)
            Assert.NotEqual(parent!.Id, wrapper.Root.Id);
        }

        [Fact]
        public void Constructor_NoDetach_RenamesExistingRoot()
        {
            using var parent = _source.StartActivity("OriginalRoot");
            using var wrapper = new RootActivityWrapper("RenamedRoot", _source, new ActivityOptions { ForceNewDetachedRoot = false });

            // The root should be renamed
            Assert.Equal("RenamedRoot", wrapper.Root?.DisplayName);
        }

        // Reusing an ambient root is the default path, and it skips Create() - where links are attached -
        // so caller-supplied Links used to be dropped exactly when ForceNewDetachedRoot was left alone.
        [Fact]
        public void Links_AreApplied_WhenReusingAnAmbientRoot()
        {
            using var ambient = _source.StartActivity("OriginalRoot")!;
            using var other = _source.StartActivity("Linked")!;
            var link = new ActivityLink(other.Context);

            using var wrapper = new RootActivityWrapper("RenamedRoot", _source,
                new ActivityOptions { ForceNewDetachedRoot = false, Links = [link] });

            Assert.Same(ambient, wrapper.Root);
            Assert.Contains(wrapper.Root!.Links, l => l.Context == other.Context);
        }

        [Fact]
        public void Links_AreApplied_WhenCreatingADetachedRoot()
        {
            using var other = _source.StartActivity("Linked")!;
            var link = new ActivityLink(other.Context);
            var options = Detached();
            options.Links = [link];

            using var wrapper = new RootActivityWrapper("RootOp", _source, options);

            Assert.Contains(wrapper.Root!.Links, l => l.Context == other.Context);
        }

        [Fact]
        public void Dispose_NoStatusSet_StampsOkByDefault()
        {
            Activity? root;
            using (var wrapper = new RootActivityWrapper("RootOp", _source, Detached()))
            {
                root = wrapper.Root;
            }

            Assert.NotNull(root);
            Assert.Equal(ActivityStatusCode.Ok, root!.Status);
        }

        // The override used to hard-code Ok, ignoring the opt-out the base class honours.
        [Fact]
        public void Dispose_MarkSuccessOnDisposeFalse_LeavesStatusUnset()
        {
            Activity? root;
            var options = Detached();
            options.MarkSuccessOnDispose = false;
            using (var wrapper = new RootActivityWrapper("RootOp", _source, options))
            {
                root = wrapper.Root;
            }

            Assert.Equal(ActivityStatusCode.Unset, root!.Status);
        }

        [Fact]
        public void Dispose_AfterRecordException_KeepsErrorStatus()
        {
            Activity? root;
            using (var wrapper = new RootActivityWrapper("RootOp", _source, Detached()))
            {
                root = wrapper.Root;
                wrapper.RecordException(new InvalidOperationException("boom"));
            }

            Assert.Equal(ActivityStatusCode.Error, root!.Status);
        }

        [Fact]
        public void Dispose_AfterMarkSucceeded_IsOk()
        {
            Activity? root;
            using (var wrapper = new RootActivityWrapper("RootOp", _source, Detached()))
            {
                root = wrapper.Root;
                wrapper.MarkSucceeded();
            }

            Assert.Equal(ActivityStatusCode.Ok, root!.Status);
        }

        private static ActivityOptions Detached() => new() { ForceNewDetachedRoot = true };

        [Fact]
        public void Dispose_RestoresParentActivity()
        {
            using var parent = _source.StartActivity("Parent");
            var parentRef = Activity.Current;

            var wrapper = new RootActivityWrapper("RootOp", _source, new ActivityOptions { ForceNewDetachedRoot = true });
            wrapper.Dispose();

            Assert.Equal(parentRef, Activity.Current);
        }

        [Fact]
        public void Dispose_CallsLogActivityEnd()
        {
            var log = new Mock<IActivityLogger>();
            var wrapper = new RootActivityWrapper("RootOp", _source, new ActivityOptions { Log = log.Object });
            wrapper.Dispose();
            log.Verify(l => l.LogActivityEnd("RootOp", It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public void Constructor_NullOperationName_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new RootActivityWrapper(null!, _source));
        }

        [Fact]
        public void AllPublicMethods_WithNullRoot_DoNotThrow()
        {
            // Create wrapper then null out Root to simulate OTel-disabled scenario
            var wrapper = new RootActivityWrapper("RootOp", _source, new ActivityOptions { ForceNewDetachedRoot = true });

            // Force Root/activity to null via reflection
            var rootProp = typeof(RootActivityWrapper).GetProperty("Root");
            rootProp?.SetValue(wrapper, null);
            var activityField = typeof(ActivityWrapper).GetField("_activity",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            activityField?.SetValue(wrapper, null);

            var ex = Record.Exception(() =>
            {
                wrapper.SetTag("key", "val");
                wrapper.RecordException(new InvalidOperationException("test"));
                wrapper.Dispose();
            });
            Assert.Null(ex);
        }

        [Fact]
        public void Dispose_DoubleDispose_DoesNotThrow()
        {
            var wrapper = new RootActivityWrapper("RootOp", _source);
            wrapper.Dispose();
            var ex = Record.Exception(() => wrapper.Dispose());
            Assert.Null(ex);
        }

        [Fact]
        public void Constructor_WithParentContext_LinksContext()
        {
            var parentContext = new ActivityContext(
                ActivityTraceId.CreateRandom(),
                ActivitySpanId.CreateRandom(),
                ActivityTraceFlags.Recorded);

            using var wrapper = new RootActivityWrapper("RootOp", _source, new ActivityOptions { ParentContext = parentContext });
            Assert.NotNull(wrapper.Root);
            // The parent context should appear as a link on the root activity
            Assert.Contains(wrapper.Root!.Links, link => link.Context.TraceId == parentContext.TraceId);
        }

        private sealed class FinalizerPathRootWrapper(string operationName, ActivitySource source, ActivityOptions? options = null)
            : RootActivityWrapper(operationName, source, options)
        {
            public void DisposeFromFinalizer() => Dispose(false);
        }

        [Fact]
        public void Constructor_ForceNewDetachedRoot_DoesNotLinkTheLocalParent()
        {
            using var parent = _source.StartActivity("Parent");

            using var wrapper = new RootActivityWrapper("RootOp", _source, new ActivityOptions { ForceNewDetachedRoot = true });

            Assert.Empty(wrapper.Root!.Links);
        }

        [Fact]
        public void DisposeFalse_LeavesRootRunningAndAmbientContextAlone()
        {
            using var parent = _source.StartActivity("Parent");
            var log = new Mock<IActivityLogger>();
            var wrapper = new FinalizerPathRootWrapper("RootOp", _source, new ActivityOptions { ForceNewDetachedRoot = true, Log = log.Object });

            wrapper.DisposeFromFinalizer();

            Assert.Same(wrapper.Root, Activity.Current);
            Assert.Equal(ActivityStatusCode.Unset, wrapper.Root!.Status);
            log.Verify(l => l.LogActivityEnd(It.IsAny<string>(), It.IsAny<string>()), Times.Never);

            wrapper.Dispose();
            Assert.Same(parent, Activity.Current);
        }

        [Fact]
        public void Dispose_WhenStoppingTheRootThrows_SwallowsAndStillRestoresParent()
        {
            using var throwingSource = new ActivitySource("Test.Root.ThrowingStop");
            using var throwingListener = new ActivityListener
            {
                ShouldListenTo = s => ReferenceEquals(s, throwingSource),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = _ => throw new InvalidOperationException("listener failed"),
            };
            ActivitySource.AddActivityListener(throwingListener);
            using var parent = _source.StartActivity("Parent");

            var wrapper = new RootActivityWrapper("RootOp", throwingSource, new ActivityOptions { ForceNewDetachedRoot = true });
            Assert.NotNull(wrapper.Root);

            Assert.Null(Record.Exception(() => wrapper.Dispose()));
            Assert.Same(parent, Activity.Current);
            Assert.Null(wrapper.Activity);
        }
    }
}
