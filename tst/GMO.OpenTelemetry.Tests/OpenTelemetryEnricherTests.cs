using GMO.OpenTelemetry.Serilog;
using Moq;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    [Collection("OpenTelemetryEnricherState")]
    public class OpenTelemetryEnricherTests
    {
        private static LogEvent BuildLogEvent(LogEventLevel level = LogEventLevel.Information,
            Dictionary<string, LogEventPropertyValue>? properties = null)
        {
            var props = properties?.Select(kvp => new LogEventProperty(kvp.Key, kvp.Value))
                            .ToArray()
                        ?? Array.Empty<LogEventProperty>();
            return new LogEvent(
                DateTimeOffset.UtcNow,
                level,
                null,
                MessageTemplate.Empty,
                props);
        }

        private static Mock<ILogEventPropertyFactory> BuildFactory()
        {
            var factory = new Mock<ILogEventPropertyFactory>();
            factory.Setup(f => f.CreateProperty(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()))
                .Returns<string, object, bool>((name, value, _) =>
                    new LogEventProperty(name, new ScalarValue(value)));
            return factory;
        }

        [Fact]
        public void Enrich_NoAttributeEnricher_AddsLevelOnly()
        {
            OpenTelemetryEnricher.Configure(null!);
            var enricher = new OpenTelemetryEnricher();
            var logEvent = BuildLogEvent();
            var factory = BuildFactory();

            enricher.Enrich(logEvent, factory.Object);

            Assert.True(logEvent.Properties.ContainsKey(OpenTelemetryEnricher.LevelProp));
        }

        [Fact]
        public void Enrich_LevelAlreadyPresent_DoesNotOverwrite()
        {
            OpenTelemetryEnricher.Configure(null!);
            var enricher = new OpenTelemetryEnricher();
            var logEvent = BuildLogEvent(properties: new Dictionary<string, LogEventPropertyValue>
            {
                [OpenTelemetryEnricher.LevelProp] = new ScalarValue("existing"),
            });
            var factory = BuildFactory();

            enricher.Enrich(logEvent, factory.Object);

            Assert.Equal("existing", ((ScalarValue)logEvent.Properties[OpenTelemetryEnricher.LevelProp]).Value);
        }

        [Fact]
        public void Enrich_WithAttributeEnricher_AddsAllAttributes()
        {
            var attributeEnricher = new Mock<IAttributeEnricher>();
            attributeEnricher.Setup(a => a.Enrich(null))
                .Returns(new Dictionary<string, object> { ["trace.id"] = "abc123" });
            OpenTelemetryEnricher.Configure(attributeEnricher.Object);

            var enricher = new OpenTelemetryEnricher();
            var logEvent = BuildLogEvent();
            var factory = BuildFactory();

            enricher.Enrich(logEvent, factory.Object);

            Assert.True(logEvent.Properties.ContainsKey("trace.id"));
        }

        // Every copy is one more attribute against New Relic's 255-per-record limit, which drops the whole
        // record when exceeded.
        [Fact]
        public void CustomProperties_AreNotDuplicatedUnderAPrefix()
        {
            var attributeEnricher = new Mock<IAttributeEnricher>();
            attributeEnricher.Setup(a => a.Enrich(null))
                .Returns(new Dictionary<string, object> { ["service.name"] = "svc" });
            OpenTelemetryEnricher.Configure(attributeEnricher.Object);

            var logEvent = BuildLogEvent(properties: new Dictionary<string, LogEventPropertyValue>
            {
                ["JobName"] = new ScalarValue("nightly"),
                ["Agency"] = new ScalarValue("north"),
            });

            new OpenTelemetryEnricher().Enrich(logEvent, BuildFactory().Object);

            Assert.Equal("nightly", ((ScalarValue)logEvent.Properties["JobName"]).Value);
            Assert.Equal("north", ((ScalarValue)logEvent.Properties["Agency"]).Value);
            Assert.DoesNotContain(logEvent.Properties.Keys, k => k.StartsWith("property.", StringComparison.Ordinal));
        }

        [Fact]
        public void Enrich_AttributeEnricherReturnsNull_DoesNotThrow()
        {
            var attributeEnricher = new Mock<IAttributeEnricher>();
            attributeEnricher.Setup(a => a.Enrich(null))
                .Returns((Dictionary<string, object>)null!);
            OpenTelemetryEnricher.Configure(attributeEnricher.Object);

            var enricher = new OpenTelemetryEnricher();
            var logEvent = BuildLogEvent();
            var factory = BuildFactory();

            var exception = Record.Exception(() => enricher.Enrich(logEvent, factory.Object));
            Assert.Null(exception);

            // Reset
            OpenTelemetryEnricher.Configure(null!);
        }

        [Fact]
        public void Enrich_AttributeEnricherThrows_DoesNotPropagate()
        {
            var attributeEnricher = new Mock<IAttributeEnricher>();
            attributeEnricher.Setup(a => a.Enrich(null))
                .Throws(new InvalidOperationException("enricher explosion"));
            OpenTelemetryEnricher.Configure(attributeEnricher.Object);

            var enricher = new OpenTelemetryEnricher();
            var logEvent = BuildLogEvent();
            var factory = BuildFactory();

            var exception = Record.Exception(() => enricher.Enrich(logEvent, factory.Object));
            Assert.Null(exception);
            // Level should still be added even though enrichment failed
            Assert.True(logEvent.Properties.ContainsKey(OpenTelemetryEnricher.LevelProp));

            OpenTelemetryEnricher.Configure(null!);
        }

        [Fact]
        public void Configure_SetsAttributeEnricher()
        {
            var mock = new Mock<IAttributeEnricher>();
            OpenTelemetryEnricher.Configure(mock.Object);

            var enricher = new OpenTelemetryEnricher();
            var logEvent = BuildLogEvent();
            var factory = BuildFactory();
            mock.Setup(a => a.Enrich(null)).Returns(new Dictionary<string, object>());

            enricher.Enrich(logEvent, factory.Object);

            mock.Verify(a => a.Enrich(null), Times.Once);

            // Reset
            OpenTelemetryEnricher.Configure(null!);
        }
    }
}
