using System;
using System.Diagnostics;
using System.Linq;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class CorrelationIdServiceTests : IDisposable
    {
        private readonly ActivitySource _source;
        private readonly ActivityListener _listener;

        public CorrelationIdServiceTests()
        {
            _source = new ActivitySource("Test.Correlation");
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
        public void GenerateCorrelationId_ReturnsValidGuid()
        {
            var sut = new CorrelationIdService();
            var id = sut.GenerateCorrelationId();
            Assert.True(Guid.TryParse(id, out _));
        }

        [Fact]
        public void GetCorrelationId_NoContext_GeneratesAndReturnsNew()
        {
            var sut = new CorrelationIdService();
            var id = sut.GetCorrelationId();
            Assert.False(string.IsNullOrEmpty(id));
            Assert.True(Guid.TryParse(id, out _));
        }

        [Fact]
        public void SetCorrelationId_ThenGet_ReturnsSet()
        {
            var sut = new CorrelationIdService();
            sut.SetCorrelationId("test-id-123");
            var id = sut.GetCorrelationId();
            Assert.Equal("test-id-123", id);
        }

        [Fact]
        public void SetCorrelationId_EmptyString_GeneratesValidGuid()
        {
            var sut = new CorrelationIdService();
            sut.SetCorrelationId("");
            var id = sut.GetCorrelationId();
            Assert.False(string.IsNullOrEmpty(id));
            Assert.True(Guid.TryParse(id, out _));
        }

        [Fact]
        public void GetCorrelationId_FromActivityBaggage_ReturnsIt()
        {
            var sut = new CorrelationIdService();
            using var activity = _source.StartActivity("Test");
            activity!.AddBaggage("CorrelationId", "baggage-id");

            var id = sut.GetCorrelationId();
            Assert.Equal("baggage-id", id);
        }

        [Fact]
        public void GetCorrelationId_FromActivityTag_ReturnsIt()
        {
            var sut = new CorrelationIdService();
            using var activity = _source.StartActivity("Test");
            activity!.SetTag("CorrelationId", "tag-id");

            var id = sut.GetCorrelationId();
            Assert.Equal("tag-id", id);
        }

        [Fact]
        public void SetCorrelationId_WithActivity_SetsTagAndBaggageOnActivity()
        {
            var sut = new CorrelationIdService();
            using var activity = _source.StartActivity("Test");
            sut.SetCorrelationId("set-id");

            // Verify stored directly on the activity tag and baggage, not just retrievable via service
            var tag = activity!.Tags.FirstOrDefault(t => t.Key == "CorrelationId").Value;
            var baggage = activity.GetBaggageItem("CorrelationId");
            Assert.Equal("set-id", tag);
            Assert.Equal("set-id", baggage);
            Assert.Equal("set-id", sut.GetCorrelationId());
        }

        [Fact]
        public void GetCorrelationId_FromParentActivityTag_WalksChain()
        {
            var sut = new CorrelationIdService();
            // Parent has the tag, child does not → triggers GetCorrelationIdFromParentChain
            using var parent = _source.StartActivity("Parent")!;
            parent.SetTag("CorrelationId", "parent-chain-id");
            using var child = _source.StartActivity("Child")!;
            // child has no CorrelationId tag/baggage itself, but parent does
            var id = sut.GetCorrelationId();
            Assert.Equal("parent-chain-id", id);
        }

        [Fact]
        public void GetCorrelationId_FromParentActivityBaggage_WalksChain()
        {
            var sut = new CorrelationIdService();
            using var parent = _source.StartActivity("Parent")!;
            parent.AddBaggage("CorrelationId", "parent-baggage-id");
            using var child = _source.StartActivity("Child")!;
            var id = sut.GetCorrelationId();
            Assert.Equal("parent-baggage-id", id);
        }

        [Fact]
        public void GetCorrelationId_EmptyChildBaggage_FallsBackToParentBaggage()
        {
            var sut = new CorrelationIdService();
            using var parent = _source.StartActivity("Parent")!;
            parent.AddBaggage("CorrelationId", "parent-baggage-id");
            using var child = _source.StartActivity("Child")!;
            child.AddBaggage("CorrelationId", string.Empty);

            Assert.Equal(string.Empty, child.GetBaggageItem("CorrelationId"));
            Assert.Equal("parent-baggage-id", sut.GetCorrelationId());
        }

        [Fact]
        public void GetCorrelationId_FromGrandparentActivityTag_WalksPastTheParent()
        {
            var sut = new CorrelationIdService();
            using var grandparent = _source.StartActivity("Grandparent")!;
            grandparent.SetTag("CorrelationId", "grandparent-tag-id");
            using var parent = _source.StartActivity("Parent")!;
            parent.SetTag("Unrelated", "x");
            using var child = _source.StartActivity("Child")!;

            Assert.Equal("grandparent-tag-id", sut.GetCorrelationId());
        }

        [Fact]
        public void GetCorrelationId_NoContext_FallsBackToGenerate()
        {
            // With no activity and no stored value, generates a fresh GUID
            var sut = new CorrelationIdService();
            var id = sut.GetCorrelationId();
            Assert.False(string.IsNullOrEmpty(id));
            Assert.True(Guid.TryParse(id, out _));
        }

        [Fact]
        public void GetCorrelationId_Consistent_ReturnsSameId()
        {
            var sut = new CorrelationIdService();
            var id1 = sut.GetCorrelationId();
            var id2 = sut.GetCorrelationId();
            Assert.Equal(id1, id2);
        }
    }
}
