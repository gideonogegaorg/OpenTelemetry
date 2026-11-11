using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Moq;
using OpenTelemetry.Trace;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class ActivityWrapperTests : IDisposable
    {
        private static readonly int[] SampleIds = [1, 2, 3];
        private static readonly int[] DuplicateIds = [1, 2, 2, 3, 1];
        private static readonly int[] UnorderedDuplicateIds = [3, 1, 3, 2, 1];
        private static readonly string[] DuplicateKeys = ["a", "b", "a"];

        private readonly ActivitySource _source;
        private readonly ActivityListener _listener;

        public ActivityWrapperTests()
        {
            _source = new ActivitySource("Test.Source");
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
        public void Constructor_SetsOperationName()
        {
            using var wrapper = new ActivityWrapper("MyOp", _source);
            Assert.NotNull(wrapper.Activity);
            Assert.Equal("MyOp", wrapper.Activity.OperationName);
        }

        [Fact]
        public void Constructor_SetsSpanDepthTag()
        {
            using var wrapper = new ActivityWrapper("MyOp", _source);
            Assert.Equal(1, wrapper.Activity?.GetTagItem("span.depth"));
        }

        [Fact]
        public void Constructor_NullOperationName_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new ActivityWrapper(null!, _source));
        }

        [Fact]
        public void Constructor_NullActivitySource_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new ActivityWrapper("op", null!));
        }

        [Fact]
        public void Constructor_CallsLogActivityStart()
        {
            var log = new Mock<IActivityLogger>();
            using var wrapper = new ActivityWrapper("MyOp", _source, new ActivityOptions { Log = log.Object });
            log.Verify(l => l.LogActivityStart("MyOp"), Times.Once);
        }

        [Fact]
        public void Constructor_SetsAdditionalTags()
        {
            var data = new Dictionary<string, object> { ["foo"] = "bar" };
            using var wrapper = new ActivityWrapper("MyOp", _source, new ActivityOptions { AdditionalData = data });
            Assert.Equal("bar", wrapper.Activity?.GetTagItem("foo"));
        }

        [Fact]
        public void SetTag_PrimitiveValue_SetsDirectly()
        {
            using var wrapper = new ActivityWrapper("MyOp", _source);
            wrapper.SetTag("key", 42);
            Assert.Equal(42, wrapper.Activity?.GetTagItem("key"));
        }

        [Fact]
        public void SetTag_ComplexObject_SerializesToJson()
        {
            using var wrapper = new ActivityWrapper("MyOp", _source);
            wrapper.SetTag("key", new { Name = "test" });
            var val = wrapper.Activity?.GetTagItem("key") as string;
            Assert.Contains("test", val);
        }

        [Fact]
        public void SetTag_CircularReference_FallsBackToToString()
        {
            using var wrapper = new ActivityWrapper("MyOp", _source);
            var circular = new Dictionary<string, object>();
            circular["self"] = circular; // circular reference
            var exception = Record.Exception(() => wrapper.SetTag("key", circular));
            Assert.Null(exception);
            // Should fallback to ToString() instead of throwing
            Assert.NotNull(wrapper.Activity?.GetTagItem("key"));
        }

        [Fact]
        public void AllPublicMethods_WithNullActivity_DoNotThrow()
        {
            // Simulate the _activity == null path by disposing the activity first
            // then calling methods -- they should all early-return safely
            using var wrapper = new ActivityWrapper("MyOp", _source);
            wrapper.Activity?.Dispose();

            // Force _activity to null via reflection for thorough testing
            var field = typeof(ActivityWrapper).GetField("_activity",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(wrapper, null);

            var ex = Record.Exception(() =>
            {
                wrapper.SetTag("key", "value");
                wrapper.SetTag("complex", new { Foo = "bar" });
                wrapper.SetStatus(ActivityStatusCode.Ok);
                wrapper.RecordException(new InvalidOperationException("test"));
                wrapper.AddEvent("test-event");
            });
            Assert.Null(ex);
        }

        [Fact]
        public void SetTag_NullValue_SetsTagDirectly()
        {
            // SetTag with null value should not throw and sets the tag as null
            using var wrapper = new ActivityWrapper("MyOp", _source);
            var exception = Record.Exception(() => wrapper.SetTag("key", null));
            Assert.Null(exception);
            Assert.Null(wrapper.Activity?.GetTagItem("key"));
        }

        [Fact]
        public void SetTag_NullableInt_SetsDirectly()
        {
            using var wrapper = new ActivityWrapper("MyOp", _source);
            int? value = 42;
            var exception = Record.Exception(() => wrapper.SetTag("key", value));
            Assert.Null(exception);
            Assert.Equal(42, wrapper.Activity?.GetTagItem("key"));
        }

        [Fact]
        public void SetTag_NullableGuid_SetsDirectly()
        {
            using var wrapper = new ActivityWrapper("MyOp", _source);
            Guid? value = Guid.Empty;
            var exception = Record.Exception(() => wrapper.SetTag("key", value));
            Assert.Null(exception);
        }

        [Fact]
        public void RecordException_SetsErrorStatusAndTags()
        {
            using var wrapper = new ActivityWrapper("MyOp", _source);
            var ex = new InvalidOperationException("oops");
            wrapper.RecordException(ex);

            Assert.Equal(ActivityStatusCode.Error, wrapper.Activity?.Status);
            Assert.Equal(ex.GetType().FullName, wrapper.Activity?.GetTagItem("exception.type"));
            Assert.Equal("oops", wrapper.Activity?.GetTagItem("exception.message"));
        }

        [Fact]
        public void RecordException_CallsLogger()
        {
            var log = new Mock<IActivityLogger>();
            using var wrapper = new ActivityWrapper("MyOp", _source, new ActivityOptions { Log = log.Object });
            var ex = new Exception("fail");
            wrapper.RecordException(ex);
            log.Verify(l => l.LogException("MyOp", ex, null), Times.Once);
        }

        [Fact]
        public void RecordException_NullEx_NoMessage_StatusUnchanged()
        {
            using var wrapper = new ActivityWrapper("MyOp", _source);
            wrapper.RecordException(null!);
            // No exception, no message — status must remain Unset
            Assert.Equal(ActivityStatusCode.Unset, wrapper.Activity?.Status);
        }

        [Fact]
        public void RecordException_NullEx_WithMessage_SetsErrorStatus()
        {
            using var wrapper = new ActivityWrapper("MyOp", _source);
            wrapper.RecordException(null!, "something went wrong");
            // Message supplied without exception — should still mark the activity as errored
            Assert.Equal(ActivityStatusCode.Error, wrapper.Activity?.Status);
            Assert.Equal("something went wrong", wrapper.Activity?.StatusDescription);
        }

        [Fact]
        public void AddEvent_NoAttributes_AddsEvent()
        {
            using var wrapper = new ActivityWrapper("MyOp", _source);
            wrapper.AddEvent("MyEvent");
            var events = wrapper.Activity?.Events;
            Assert.Contains(events!, e => e.Name == "MyEvent");
        }

        [Fact]
        public void AddEvent_WithAttributes_AddsEventWithTags()
        {
            using var wrapper = new ActivityWrapper("MyOp", _source);
            wrapper.AddEvent("MyEvent", new Dictionary<string, object> { ["k"] = "v" });
            var matchingEvent = wrapper.Activity?.Events.FirstOrDefault(e => e.Name == "MyEvent");
            Assert.NotNull(matchingEvent);
            Assert.Equal("v", matchingEvent.Value.Tags.FirstOrDefault(t => t.Key == "k").Value);
        }


        [Fact]
        public void Dispose_CallsLogActivityEnd()
        {
            var log = new Mock<IActivityLogger>();
            var wrapper = new ActivityWrapper("MyOp", _source, new ActivityOptions { Log = log.Object });
            wrapper.Dispose();
            log.Verify(l => l.LogActivityEnd("MyOp", It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public void Dispose_DoubleDispose_DoesNotThrow()
        {
            var wrapper = new ActivityWrapper("MyOp", _source);
            wrapper.Dispose();
            var ex = Record.Exception(() => wrapper.Dispose());
            Assert.Null(ex);
        }

        [Fact]
        public void SetStatus_SetsActivityStatus()
        {
            using var wrapper = new ActivityWrapper("MyOp", _source);
            wrapper.SetStatus(ActivityStatusCode.Error, "desc");
            Assert.Equal(ActivityStatusCode.Error, wrapper.Activity?.Status);
        }

        [Fact]
        public void RecordException_LoggerThrows_DoesNotPropagate()
        {
            var log = new Mock<IActivityLogger>();
            log.Setup(l => l.LogException(It.IsAny<string>(), It.IsAny<Exception>(), It.IsAny<string?>()))
                .Throws(new InvalidOperationException("logger failure"));
            using var wrapper = new ActivityWrapper("MyOp", _source, new ActivityOptions { Log = log.Object });
            var ex = new Exception("test error");
            var exception = Record.Exception(() => wrapper.RecordException(ex));
            Assert.Null(exception);
            // Exception tags should still be set despite logger failure
            Assert.Equal(ActivityStatusCode.Error, wrapper.Activity?.Status);
        }

        // The default has to stay Ok: hand-rolled wrappers across the consuming repos rely on it and
        // would otherwise all flip to Unset without touching their code.
        [Fact]
        public void Dispose_NoStatusSet_StampsOkByDefault()
        {
            Activity activity;
            using (var wrapper = new ActivityWrapper("MyOp", _source))
            {
                activity = wrapper.Activity!;
                Assert.NotNull(activity);
            }

            Assert.Equal(ActivityStatusCode.Ok, activity.Status);
        }

        [Fact]
        public void Dispose_NoStatusSet_MarkSuccessOnDisposeFalse_LeavesStatusUnset()
        {
            Activity activity;
            var options = new ActivityOptions { MarkSuccessOnDispose = false };
            using (var wrapper = new ActivityWrapper("MyOp", _source, options))
            {
                activity = wrapper.Activity!;
            }

            Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        }

        [Fact]
        public void Dispose_ExplicitOk_IsPreserved()
        {
            Activity activity;
            using (var wrapper = new ActivityWrapper("MyOp", _source))
            {
                activity = wrapper.Activity!;
                wrapper.SetStatus(ActivityStatusCode.Ok);
            }

            Assert.Equal(ActivityStatusCode.Ok, activity.Status);
        }

        [Fact]
        public void Dispose_ErrorStatus_IsNotOverwritten()
        {
            Activity activity;
            using (var wrapper = new ActivityWrapper("MyOp", _source))
            {
                activity = wrapper.Activity!;
                wrapper.RecordException(new InvalidOperationException("boom"));
            }

            Assert.Equal(ActivityStatusCode.Error, activity.Status);
        }
        [Fact]
        public void SetListTags_NullItems_SetsNothing()
        {
            using var wrapper = new ActivityWrapper("MyOp", _source);
            wrapper.SetListTags<int>(null, "ids");
            Assert.Null(wrapper.Activity?.GetTagItem("ids"));
            Assert.Null(wrapper.Activity?.GetTagItem("idsCount"));
        }

        [Fact]
        public void SetListTags_EmptyCollection_SetsZeroCount()
        {
            using var wrapper = new ActivityWrapper("MyOp", _source);
            wrapper.SetListTags(new List<int>(), "ids");
            Assert.Equal(0, wrapper.Activity?.GetTagItem("idsCount"));
        }

        // Event attributes went to the exporter unrendered, so a collection or complex value arrived as
        // its type name - the same failure SetTag already guarded against.
        [Fact]
        public void AddEvent_ComplexAttribute_IsSerializedNotToStringed()
        {
            using var wrapper = new ActivityWrapper("MyOp", _source);

            wrapper.AddEvent("Processed", new Dictionary<string, object>
            {
                ["ids"] = SampleIds,
                ["detail"] = new { Name = "a" },
            });

            var tags = Assert.Single(wrapper.Activity!.Events).Tags.ToDictionary(t => t.Key, t => t.Value);
            Assert.Equal("[1,2,3]", Assert.IsType<string>(tags["ids"]));
            Assert.Equal("{\"Name\":\"a\"}", Assert.IsType<string>(tags["detail"]));
        }

        [Fact]
        public void AddEvent_PrimitiveAttributes_ArePassedThroughUnchanged()
        {
            using var wrapper = new ActivityWrapper("MyOp", _source);

            wrapper.AddEvent("Processed", new Dictionary<string, object>
            {
                ["count"] = 3,
                ["label"] = "ok",
            });

            var tags = Assert.Single(wrapper.Activity!.Events).Tags.ToDictionary(t => t.Key, t => t.Value);
            Assert.Equal(3, tags["count"]);
            Assert.Equal("ok", tags["label"]);
        }

        // Activity.SetTag/AddTag return Activity so call sites can chain; the wrapper returning void
        // forced every converted chain to be unrolled.
        [Fact]
        public void Mutators_AreFluent_AndChainOnTheSameWrapper()
        {
            using var wrapper = new ActivityWrapper("MyOp", _source);

            var returned = wrapper
                .SetTag("user.id", 7)
                .SetListTags(SampleIds, "ids")
                .AddEvent("Started")
                .MarkSucceeded();

            Assert.Same(wrapper, returned);
            Assert.Equal(7, wrapper.Activity?.GetTagItem("user.id"));
            Assert.Equal("[1,2,3]", wrapper.Activity?.GetTagItem("ids"));
            Assert.Equal(3, wrapper.Activity?.GetTagItem("idsCount"));
            Assert.Single(wrapper.Activity!.Events);
            Assert.Equal(ActivityStatusCode.Ok, wrapper.Activity!.Status);
        }

        // The chain must not break when there is no activity to write to, which is the shape every
        // OTel-disabled host runs in.
        [Fact]
        public void Mutators_AreFluent_WhenActivityIsNull()
        {
            using var wrapper = new ActivityWrapper("MyOp", _source);
            typeof(ActivityWrapper)
                .GetField("_activity", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(wrapper, null);
            Assert.Null(wrapper.Activity);

            var returned = wrapper
                .SetTag("k", "v")
                .SetListTags(SampleIds, "ids")
                .AddEvent("Started")
                .SetStatus(ActivityStatusCode.Ok)
                .RecordException(new InvalidOperationException("boom"))
                .MarkSucceeded();

            Assert.Same(wrapper, returned);
        }

        [Fact]
        public void AddEvent_WithoutAttributes_RecordsBareEvent()
        {
            using var wrapper = new ActivityWrapper("MyOp", _source);

            wrapper.AddEvent("Started");

            var evt = Assert.Single(wrapper.Activity!.Events);
            Assert.Equal("Started", evt.Name);
            Assert.Empty(evt.Tags);
        }

        [Fact]
        public void SetListTags_Primitives_SetsCountAndJsonString()
        {
            using var wrapper = new ActivityWrapper("MyOp", _source);
            wrapper.SetListTags(SampleIds, "ids");

            Assert.Equal(3, wrapper.Activity?.GetTagItem("idsCount"));
            // A string, not an int[]: TruncatingSpanProcessor only ever sees string-valued tags, so a
            // raw array would skip both the length clamp and the AdditionalKeysToLog overflow log.
            var ids = Assert.IsType<string>(wrapper.Activity?.GetTagItem("ids"));
            Assert.Equal("[1,2,3]", ids);
        }

        [Fact]
        public void SetListTags_ComplexElements_SerializesElementsRatherThanToString()
        {
            using var wrapper = new ActivityWrapper("MyOp", _source);
            wrapper.SetListTags([new { Name = "a" }, new { Name = "b" }], "items");

            Assert.Equal(2, wrapper.Activity?.GetTagItem("itemsCount"));
            var items = Assert.IsType<string>(wrapper.Activity?.GetTagItem("items"));
            Assert.Contains("a", items);
            Assert.Contains("b", items);
            // The failure this guards: joining would call ToString() on each element and emit the type
            // name instead of the content.
            Assert.DoesNotContain("<>f__AnonymousType", items);
        }

        // An identifier list is the whole reason SetListTags exists, and a repeated id carries no
        // information while inflating both the count and the payload.
        [Fact]
        public void SetListTags_DuplicatePrimitives_AreDeduplicated()
        {
            using var wrapper = new ActivityWrapper("MyOp", _source);
            wrapper.SetListTags(DuplicateIds, "ids");

            Assert.Equal(3, wrapper.Activity?.GetTagItem("idsCount"));
            Assert.Equal("[1,2,3]", Assert.IsType<string>(wrapper.Activity?.GetTagItem("ids")));
        }

        [Fact]
        public void SetListTags_DuplicatePrimitives_KeepFirstOccurrenceOrder()
        {
            using var wrapper = new ActivityWrapper("MyOp", _source);
            wrapper.SetListTags(UnorderedDuplicateIds, "ids");

            Assert.Equal("[3,1,2]", Assert.IsType<string>(wrapper.Activity?.GetTagItem("ids")));
        }

        [Fact]
        public void SetListTags_DuplicateStrings_AreDeduplicated()
        {
            using var wrapper = new ActivityWrapper("MyOp", _source);
            wrapper.SetListTags(DuplicateKeys, "keys");

            Assert.Equal(2, wrapper.Activity?.GetTagItem("keysCount"));
        }

        // Guid and Nullable<T> are not Type.IsPrimitive, so they only de-duplicate because
        // IsPrimitiveType names them explicitly - the branch a plain IsPrimitive check would miss.
        [Fact]
        public void SetListTags_DuplicateGuids_AreDeduplicated()
        {
            var id = Guid.NewGuid();
            using var wrapper = new ActivityWrapper("MyOp", _source);
            wrapper.SetListTags([id, Guid.NewGuid(), id], "userIds");

            Assert.Equal(2, wrapper.Activity?.GetTagItem("userIdsCount"));
        }

        [Fact]
        public void SetListTags_DuplicateNullableIds_AreDeduplicated()
        {
            using var wrapper = new ActivityWrapper("MyOp", _source);
            wrapper.SetListTags(new int?[] { 1, null, 1, null, 2 }, "ids");

            Assert.Equal(3, wrapper.Activity?.GetTagItem("idsCount"));
        }

        // Complex elements default to reference equality, so Distinct would drop nothing while still
        // costing a hash pass - and a caller tagging complex values may depend on multiplicity.
        [Fact]
        public void SetListTags_DuplicateComplexElements_AreKept()
        {
            var one = new StringBuilder("a");
            using var wrapper = new ActivityWrapper("MyOp", _source);
            wrapper.SetListTags([one, one, new StringBuilder("b")], "items");

            Assert.Equal(3, wrapper.Activity?.GetTagItem("itemsCount"));
        }

        [Fact]
        public void SetListTags_DictionaryKeyCollection_SerializesKeys()
        {
            using var wrapper = new ActivityWrapper("MyOp", _source);
            var map = new Dictionary<int, string> { { 7, "a" }, { 9, "b" } };

            wrapper.SetListTags(map.Keys, "documentIds");

            Assert.Equal(2, wrapper.Activity?.GetTagItem("documentIdsCount"));
            var ids = Assert.IsType<string>(wrapper.Activity?.GetTagItem("documentIds"));
            Assert.Equal("[7,9]", ids);
            Assert.DoesNotContain("KeyCollection", ids);
        }

        [Fact]
        public void SetListTags_LazySequence_EnumeratesOnce()
        {
            using var wrapper = new ActivityWrapper("MyOp", _source);
            var enumerations = 0;
            IEnumerable<int> Lazy()
            {
                enumerations++;
                yield return 1;
                yield return 2;
            }

            wrapper.SetListTags(Lazy(), "ids");

            Assert.Equal(1, enumerations);
            Assert.Equal(2, wrapper.Activity?.GetTagItem("idsCount"));
        }

        #region Raw-Activity members these wrap

        [Fact]
        public void Context_ExposesTheUnderlyingSpanContext()
        {
            using var wrapper = new ActivityWrapper("op", _source);

            // Why it is exposed: hand this to ActivityOptions.ParentContext at the far end so work
            // starting elsewhere is parented to this span.
            //
            // Deliberately not asserting a non-default SpanId: under the hierarchical id format every
            // Context is default, and net48 stays hierarchical until some other test class builds a
            // TracerProvider, which would make that assertion a race.
            Assert.Equal(wrapper.Activity!.Context, wrapper.Context);
        }

        [Fact]
        public void Stop_HandsTheSpanToTheSdk_SoALaterStatusIsNotExported()
        {
            var statusWhenExported = (ActivityStatusCode)(-1);
            using var probe = new ActivityListener
            {
                ShouldListenTo = s => s.Name == _source.Name,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = a => statusWhenExported = a.Status,
            };
            ActivitySource.AddActivityListener(probe);

            var wrapper = new ActivityWrapper("op", _source);
            wrapper.Stop();
            wrapper.Dispose();

            // Dispose marks success by default, but Stop already handed the span to the SDK and no
            // second callback fires, so the exported status stays Unset while the object reads Ok.
            // Anything that must be exported has to be set before stopping early.
            Assert.Equal(ActivityStatusCode.Unset, statusWhenExported);
            Assert.Equal(ActivityStatusCode.Ok, wrapper.Activity!.Status);
        }

        [Fact]
        public void Context_IsNull_WhenNoActivityWasCreated()
        {
            using var wrapper = new ActivityWrapper("op", _source);
            typeof(ActivityWrapper)
                .GetField("_activity", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(wrapper, null);

            Assert.Null(wrapper.Context);
        }

        [Fact]
        public void Stop_EndsTheSpan()
        {
            using var wrapper = new ActivityWrapper("op", _source);
            Assert.Equal(default, wrapper.Activity!.Duration);

            wrapper.Stop();

            Assert.NotEqual(default, wrapper.Activity.Duration);
        }

        [Fact]
        public void Stop_DoesNotMarkSuccess()
        {
            using var wrapper = new ActivityWrapper("op", _source, new ActivityOptions { MarkSuccessOnDispose = false });

            wrapper.Stop();

            // Stop is not Dispose: it ends timing only and must not claim an outcome.
            Assert.Equal(ActivityStatusCode.Unset, wrapper.Activity!.Status);
        }

        [Fact]
        public void Stop_IsSafeBeforeDispose()
        {
            var wrapper = new ActivityWrapper("op", _source);
            wrapper.Stop();

            // The enclosing using still disposes an already-stopped span; that must not throw.
            Assert.Null(Record.Exception(() => wrapper.Dispose()));
        }

        [Fact]
        public void AddBaggage_IsReadableFromTheActivity()
        {
            using var wrapper = new ActivityWrapper("op", _source);

            wrapper.AddBaggage("tenant", "acme");

            Assert.Equal("acme", wrapper.Activity!.GetBaggageItem("tenant"));
        }

        [Fact]
        public void AddLink_AddsTheLink()
        {
            using var other = _source.StartActivity("linked")!;
            using var wrapper = new ActivityWrapper("op", _source);

            wrapper.AddLink(new ActivityLink(other.Context));

            Assert.Contains(wrapper.Activity!.Links, l => l.Context == other.Context);
        }

        [Fact]
        public void AddEvent_ActivityEvent_IsAddedAsGiven()
        {
            using var wrapper = new ActivityWrapper("op", _source);

            wrapper.AddEvent(new ActivityEvent("custom", tags: new ActivityTagsCollection { { "k", "v" } }));

            var ev = Assert.Single(wrapper.Activity!.Events);
            Assert.Equal("custom", ev.Name);
            Assert.Equal("v", ev.Tags.Single().Value);
        }

        [Fact]
        public void AddEvent_ActivityEvent_DoesNotRenderTagValues()
        {
            using var wrapper = new ActivityWrapper("op", _source);
            var ids = new List<int> { 1, 2 };

            // The caller owns the tags on this overload; the (string, Dictionary) overload runs values
            // through the same rendering as SetTag. Pinned so the difference stays deliberate.
            wrapper.AddEvent(new ActivityEvent("raw", tags: new ActivityTagsCollection { { "ids", ids } }));
            wrapper.AddEvent("rendered", new Dictionary<string, object> { ["ids"] = ids });

            Assert.Same(ids, wrapper.Activity!.Events.First(e => e.Name == "raw").Tags.Single().Value);
            Assert.Equal("[1,2]", wrapper.Activity.Events.First(e => e.Name == "rendered").Tags.Single().Value);
        }

        [Fact]
        public void RawMembers_ReturnTheWrapperSoTheyChain()
        {
            using var wrapper = new ActivityWrapper("op", _source);

            var chained = wrapper
                .AddBaggage("k", "v")
                .AddEvent(new ActivityEvent("e"))
                .SetTag("t", 1)
                .Stop();

            Assert.Same(wrapper, chained);
        }

        #endregion

        private sealed class NoActivityWrapper : ActivityWrapper
        {
            public NoActivityWrapper(string operationName, ActivitySource source, ActivityOptions? options = null)
                : base(operationName, source, options) => Initialize();

            protected override Activity? StartActivity() => null;
        }

        private sealed class FinalizerPathWrapper : ActivityWrapper
        {
            public FinalizerPathWrapper(string operationName, ActivitySource source, ActivityOptions? options = null)
                : base(operationName, source, options) => Initialize();

            public void DisposeFromFinalizer() => Dispose(false);
        }

        [Fact]
        public void NoActivity_RawMembersAreNoOps_AndEndIsLoggedAsUnknown()
        {
            var log = new Mock<IActivityLogger>();
            var wrapper = new NoActivityWrapper("op", _source, new ActivityOptions { Log = log.Object });

            var ex = Record.Exception(() => wrapper
                .AddBaggage("k", "v")
                .AddLink(new ActivityLink(new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded)))
                .AddEvent(new ActivityEvent("e"))
                .MarkSucceeded()
                .SetListTags(SampleIds, "ids")
                .Stop());
            wrapper.Dispose();

            Assert.Null(ex);
            Assert.Null(wrapper.Activity);
            Assert.Null(wrapper.Context);
            log.Verify(l => l.LogActivityStart("op"), Times.Once);
            log.Verify(l => l.LogActivityEnd("op", "Unknown"), Times.Once);
        }

        [Fact]
        public void LoggerThrowsOnStartAndEnd_SpanStillRuns_AndDisposeCompletes()
        {
            var log = new Mock<IActivityLogger>();
            log.Setup(l => l.LogActivityStart(It.IsAny<string>())).Throws(new InvalidOperationException("start failed"));
            log.Setup(l => l.LogActivityEnd(It.IsAny<string>(), It.IsAny<string>())).Throws(new InvalidOperationException("end failed"));
            using var parent = _source.StartActivity("ambient");

            var wrapper = new ActivityWrapper("op", _source, new ActivityOptions { Log = log.Object, ForceNewDetachedRoot = true });
            Assert.NotNull(wrapper.Activity);

            Assert.Null(Record.Exception(() => wrapper.Dispose()));
            Assert.Equal(ActivityStatusCode.Ok, wrapper.Activity!.Status);
            Assert.Same(parent, Activity.Current);
            log.Verify(l => l.LogActivityEnd("op", "Ok"), Times.Once);
        }

        [Fact]
        public void DisposeFalse_LeavesTheSpanUntouched()
        {
            var log = new Mock<IActivityLogger>();
            var wrapper = new FinalizerPathWrapper("op", _source, new ActivityOptions { Log = log.Object });

            wrapper.DisposeFromFinalizer();

            Assert.Equal(ActivityStatusCode.Unset, wrapper.Activity!.Status);
            Assert.Same(wrapper.Activity, Activity.Current);
            log.Verify(l => l.LogActivityEnd(It.IsAny<string>(), It.IsAny<string>()), Times.Never);

            wrapper.Dispose();
            Assert.Equal(ActivityStatusCode.Ok, wrapper.Activity.Status);
        }

        [Fact]
        public void RecordException_WithMessage_UsesMessageAsStatusDescription()
        {
            using var wrapper = new ActivityWrapper("op", _source);

            wrapper.RecordException(new InvalidOperationException("raw failure"), "friendly summary");

            Assert.Equal(ActivityStatusCode.Error, wrapper.Activity!.Status);
            Assert.Equal("friendly summary", wrapper.Activity.StatusDescription);
            Assert.Equal("raw failure", wrapper.Activity.GetTagItem("exception.message"));
        }
    }
}
