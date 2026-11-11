using System;
using System.Diagnostics;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    /// <summary>Regression cover: ActivityWrapper used to drop both ParentContext and ForceNewDetachedRoot.</summary>
    public class ActivityWrapperParentingTests : IDisposable
    {
        private readonly ActivitySource _source;
        private readonly ActivityListener _listener;

        public ActivityWrapperParentingTests()
        {
            _source = new ActivitySource("Test.Parenting");
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

        private static ActivityContext RemoteContext() => new ActivityContext(
            ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded, isRemote: true);

        [Fact]
        public void ParentContext_IsHonoured()
        {
            var remote = RemoteContext();

            using var wrapper = new ActivityWrapper("op", _source, new ActivityOptions { ParentContext = remote });

            Assert.Equal(remote.TraceId, wrapper.Activity!.TraceId);
            Assert.Equal(remote.SpanId, wrapper.Activity.ParentSpanId);
        }

        [Fact]
        public void ParentContext_TakesPrecedenceOverAmbientActivity()
        {
            using var ambient = _source.StartActivity("ambient");
            var remote = RemoteContext();

            using var wrapper = new ActivityWrapper("op", _source, new ActivityOptions { ParentContext = remote });

            Assert.Equal(remote.TraceId, wrapper.Activity!.TraceId);
            Assert.NotEqual(ambient!.TraceId, wrapper.Activity.TraceId);
        }

        [Fact]
        public void WithoutParentContext_StillInheritsAmbientActivity()
        {
            using var ambient = _source.StartActivity("ambient");

            using var wrapper = new ActivityWrapper("op", _source);

            Assert.Equal(ambient!.TraceId, wrapper.Activity!.TraceId);
            Assert.Equal(ambient.SpanId, wrapper.Activity.ParentSpanId);
        }

        [Fact]
        public void ForceNewDetachedRoot_DetachesFromAmbientActivity()
        {
            using var ambient = _source.StartActivity("ambient");

            using var wrapper = new ActivityWrapper("op", _source, new ActivityOptions { ForceNewDetachedRoot = true });

            Assert.Null(wrapper.Activity!.Parent);
            Assert.Null(wrapper.Activity.ParentId);
            Assert.NotEqual(ambient!.Id, wrapper.Activity.Id);
        }

        [Fact]
        public void ForceNewDetachedRoot_RestoresAmbientActivityOnDispose()
        {
            using var ambient = _source.StartActivity("ambient");

            using (var wrapper = new ActivityWrapper("op", _source, new ActivityOptions { ForceNewDetachedRoot = true }))
            {
                Assert.NotSame(ambient, Activity.Current);
            }

            Assert.Same(ambient, Activity.Current);
        }

        [Fact]
        public void ForceNewDetachedRoot_WithParentContext_UsesTheRemoteParentOnly()
        {
            using var ambient = _source.StartActivity("ambient");
            var remote = RemoteContext();

            using var wrapper = new ActivityWrapper("op", _source,
                new ActivityOptions { ForceNewDetachedRoot = true, ParentContext = remote });

            Assert.Equal(remote.TraceId, wrapper.Activity!.TraceId);
            Assert.NotEqual(ambient!.TraceId, wrapper.Activity.TraceId);
        }

        [Fact]
        public void ForceNewDetachedRoot_WithNoAmbientActivity_StartsACleanRoot()
        {
            using var wrapper = new ActivityWrapper("op", _source, new ActivityOptions { ForceNewDetachedRoot = true });

            Assert.NotNull(wrapper.Activity);
            Assert.Null(wrapper.Activity!.ParentId);
        }
    }
}
