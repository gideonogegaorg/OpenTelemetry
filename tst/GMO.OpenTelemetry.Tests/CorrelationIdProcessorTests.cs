using System;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using OpenTelemetry;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class CorrelationIdProcessorTests : IDisposable
    {
        private readonly ActivitySource _source;
        private readonly ActivityListener _listener;

        public CorrelationIdProcessorTests()
        {
            _source = new ActivitySource("Test.Processor");
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

        private static ServiceProvider BuildServiceProvider(ICorrelationIdService svc = null!)
        {
            var services = new ServiceCollection();
            if (svc != null)
                services.AddSingleton(svc);
            return services.BuildServiceProvider();
        }

        [Fact]
        public void OnStart_ActivityAlreadyHasTag_Skips()
        {
            var svc = new Mock<ICorrelationIdService>();
            var processor = new CorrelationIdProcessor(BuildServiceProvider(svc.Object));
            using var activity = _source.StartActivity("Test")!;
            activity.SetTag("CorrelationId", "existing");

            processor.OnStart(activity);

            svc.Verify(s => s.GetCorrelationId(), Times.Never);
        }

        [Fact]
        public void OnStart_FromBaggage_SetsTag()
        {
            var processor = new CorrelationIdProcessor(BuildServiceProvider());
            using var activity = _source.StartActivity("Test")!;
            Baggage.SetBaggage("CorrelationId", "baggage-id");

            processor.OnStart(activity);

            Assert.Equal("baggage-id", activity.GetTagItem("CorrelationId"));
            Baggage.SetBaggage("CorrelationId", null);
        }

        [Fact]
        public void OnStart_FromService_SetsTag()
        {
            var svc = new Mock<ICorrelationIdService>();
            svc.Setup(s => s.GetCorrelationId()).Returns("svc-id");
            var processor = new CorrelationIdProcessor(BuildServiceProvider(svc.Object));
            using var activity = _source.StartActivity("Test")!;

            processor.OnStart(activity);

            Assert.Equal("svc-id", activity.GetTagItem("CorrelationId"));
        }

        [Fact]
        public void OnStart_NoService_DoesNotThrow()
        {
            var processor = new CorrelationIdProcessor(BuildServiceProvider());
            using var activity = _source.StartActivity("Test")!;
            var exception = Record.Exception(() => processor.OnStart(activity));
            Assert.Null(exception);
            // No service registered, no baggage set — tag should not be set
            Assert.Null(activity.GetTagItem("CorrelationId"));
        }

        [Fact]
        public void OnStart_FromParentTag_SetsTag()
        {
            var processor = new CorrelationIdProcessor(BuildServiceProvider());
            using var parent = _source.StartActivity("Parent")!;
            parent.SetTag("CorrelationId", "parent-id");

            using var child = _source.StartActivity("Child")!;

            processor.OnStart(child);

            Assert.Equal("parent-id", child.GetTagItem("CorrelationId"));
        }

        [Fact]
        public void Constructor_NullServiceProvider_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new CorrelationIdProcessor(null!));
        }

        [Fact]
        public void OnStart_ServiceThrows_DoesNotPropagate()
        {
            var svc = new Mock<ICorrelationIdService>();
            svc.Setup(s => s.GetCorrelationId()).Throws(new InvalidOperationException("fail"));
            var processor = new CorrelationIdProcessor(BuildServiceProvider(svc.Object));
            using var activity = _source.StartActivity("Test")!;
            var exception = Record.Exception(() => processor.OnStart(activity));
            Assert.Null(exception);
        }

        [Fact]
        public void OnStart_DeepParentChain_DoesNotHang()
        {
            var processor = new CorrelationIdProcessor(BuildServiceProvider());

            // Create a chain of 60 activities (beyond the 50-depth guard)
            var activities = new Activity?[60];
            activities[0] = _source.StartActivity("Level0");

            for (int i = 1; i < 60; i++)
            {
                activities[i] = _source.StartActivity($"Level{i}");
            }

            // Should complete without hanging
            var exception = Record.Exception(() => processor.OnStart(activities[59]!));
            Assert.Null(exception);

            // Clean up in reverse order
            for (int i = 59; i >= 0; i--)
            {
                activities[i]?.Dispose();
            }
        }
    }
}
