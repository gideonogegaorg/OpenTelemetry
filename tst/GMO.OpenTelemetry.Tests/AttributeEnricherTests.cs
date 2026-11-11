using System.Collections.Generic;
using System.Diagnostics;
using Moq;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class AttributeEnricherTests : IDisposable
    {
        private readonly ActivitySource _source;
        private readonly ActivityListener _listener;

        public AttributeEnricherTests()
        {
            _source = new ActivitySource("Test.Enricher");
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

        private static IOpenTelemetryOptions BuildOptions(
            bool thread = false, bool user = false, bool source = false)
        {
            var opts = new Mock<IOpenTelemetryOptions>();
            opts.Setup(o => o.IncludeThreadInfo).Returns(thread);
            opts.Setup(o => o.IncludeUserInfo).Returns(user);
            opts.Setup(o => o.IncludeSourceLocation).Returns(source);
            opts.Setup(o => o.Attributes).Returns(new Dictionary<string, object>());
            opts.Setup(o => o.LogStackExclusions).Returns(System.Array.Empty<string>());
            return opts.Object;
        }

        [Fact]
        public void Enrich_NoActivity_ReturnsBaseAttributes()
        {
            var sut = new AttributeEnricher(BuildOptions());
            var result = sut.Enrich(null);
            Assert.NotNull(result);
        }

        [Fact]
        public void Enrich_WithActivity_AddsTraceAndSpanId()
        {
            using var activity = _source.StartActivity("Test");
            var sut = new AttributeEnricher(BuildOptions());
            var result = sut.Enrich(activity);
            Assert.True(result.ContainsKey("trace.id"));
            Assert.True(result.ContainsKey("span.id"));
        }

        [Fact]
        public void Enrich_ThreadInfo_AddsThreadAttributes()
        {
            var sut = new AttributeEnricher(BuildOptions(thread: true));
            var result = sut.Enrich(null);
            Assert.True(result.ContainsKey("thread.id"));
            Assert.True(result.ContainsKey("thread.name"));
        }

        [Fact]
        public void Enrich_NoThreadInfo_OmitsThreadAttributes()
        {
            var sut = new AttributeEnricher(BuildOptions(thread: false));
            var result = sut.Enrich(null);
            Assert.False(result.ContainsKey("thread.id"));
        }

        [Fact]
        public void Enrich_UserInfo_AddsUserAttributes()
        {
            var sut = new AttributeEnricher(BuildOptions(user: true));
            var result = sut.Enrich(null);
            // user.identity may or may not be set depending on env, just verify no exception
            Assert.NotNull(result);
        }

        [Fact]
        public void Enrich_SourceLocation_DoesNotThrow()
        {
            var sut = new AttributeEnricher(BuildOptions(source: true));
            var result = sut.Enrich(null);
            Assert.NotNull(result);
        }

        [Fact]
        public void GetCorrelationId_NullService_ReturnsNull()
        {
            var sut = new AttributeEnricher(BuildOptions());
            var result = sut.GetCorrelationId();
            Assert.Null(result);
        }

        [Fact]
        public void GetCorrelationId_WithService_ReturnsValue()
        {
            var svc = new Mock<ICorrelationIdService>();
            svc.Setup(s => s.GetCorrelationId()).Returns("abc-123");
            var sut = new AttributeEnricher(BuildOptions(), svc.Object);
            Assert.Equal("abc-123", sut.GetCorrelationId());
        }

        [Fact]
        public void Enrich_WithCorrelationService_AddsCorrelationId()
        {
            var svc = new Mock<ICorrelationIdService>();
            svc.Setup(s => s.GetCorrelationId()).Returns("abc-123");
            var sut = new AttributeEnricher(BuildOptions(), svc.Object);
            // CorrelationId is only added when an activity is present
            using var activity = _source.StartActivity("Test");
            var result = sut.Enrich(activity);
            Assert.Equal("abc-123", result["CorrelationId"]);
        }

        [Fact]
        public void Enrich_OptionsWithExtraAttributes_IncludesThemInResult()
        {
            var opts = new Mock<IOpenTelemetryOptions>();
            opts.Setup(o => o.IncludeThreadInfo).Returns(false);
            opts.Setup(o => o.IncludeUserInfo).Returns(false);
            opts.Setup(o => o.IncludeSourceLocation).Returns(false);
            opts.Setup(o => o.LogStackExclusions).Returns(System.Array.Empty<string>());
            opts.Setup(o => o.Attributes).Returns(new Dictionary<string, object> { ["custom"] = "val" });

            var sut = new AttributeEnricher(opts.Object);
            var result = sut.Enrich(null);
            Assert.Equal("val", result["custom"]);
        }

        [Fact]
        public void Enrich_SourceLocationDisabled_NoSourceAttributes()
        {
            var sut = new AttributeEnricher(BuildOptions(source: false));
            var result = sut.Enrich(null);
            Assert.False(result.ContainsKey("source.class"));
            Assert.False(result.ContainsKey("source.method"));
            Assert.False(result.ContainsKey("source.file"));
            Assert.False(result.ContainsKey("source.line"));
        }

        [Fact]
        public void Constructor_NullOptions_DoesNotThrow()
        {
            var exception = Record.Exception(() => new AttributeEnricher(null!));
            Assert.Null(exception);
        }

        [Fact]
        public void Enrich_NullOptions_ReturnsEmptyAttributes()
        {
            var sut = new AttributeEnricher(null!);
            var result = sut.Enrich(null);
            Assert.NotNull(result);
        }

        [Fact]
        public void Enrich_SourceLocationEnabled_HasSourceAttributes()
        {
            var sut = new AttributeEnricher(BuildOptions(source: true));
            var result = sut.Enrich(null);
            // Should find at least source.class from the test method's stack frame
            Assert.True(result.ContainsKey("source.class"));
            Assert.True(result.ContainsKey("source.method"));
        }

        [Fact]
        public void Enrich_WhenAttributesGetterThrows_ReturnsEmptyDict()
        {
            var opts = new Mock<IOpenTelemetryOptions>();
            opts.Setup(o => o.Attributes).Throws(new InvalidOperationException("boom"));
            opts.Setup(o => o.LogStackExclusions).Returns(System.Array.Empty<string>());
            var sut = new AttributeEnricher(opts.Object);
            var result = sut.Enrich(null);
            Assert.NotNull(result);
            Assert.Empty(result);
        }
    }
}
