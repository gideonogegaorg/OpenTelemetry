using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using GMO.OpenTelemetry.Log4Net;
using log4net.Core;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using MsLogger = Microsoft.Extensions.Logging.ILogger;

namespace GMO.OpenTelemetry.Tests
{
    public class OtlpLog4NetAppenderTests : IDisposable
    {
        private readonly ActivitySource _source;
        private readonly ActivityListener _listener;
        private readonly Mock<ILoggerFactory> _loggerFactory;
        private readonly Mock<MsLogger> _logger;
        private readonly Mock<IAttributeEnricher> _enricher;
        private readonly Mock<IOpenTelemetryOptions> _options;

        public OtlpLog4NetAppenderTests()
        {
            _source = new ActivitySource("Test.Log4NetAppender");
            _listener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            };
            ActivitySource.AddActivityListener(_listener);

            _logger = new Mock<MsLogger>();
            _logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            _loggerFactory = new Mock<ILoggerFactory>();
            _loggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(_logger.Object);

            _enricher = new Mock<IAttributeEnricher>();
            // Fresh instance per call, as the real AttributeEnricher does; a shared dictionary would
            // let one chunk's attributes bleed into the next.
            _enricher.Setup(e => e.Enrich(null)).Returns(() => []);

            _options = new Mock<IOpenTelemetryOptions>();
            _options.Setup(o => o.Enabled).Returns(true);
            _options.Setup(o => o.MaxLogMessageLength).Returns(1000);
        }

        public void Dispose()
        {
            _listener.Dispose();
            _source.Dispose();
            GC.SuppressFinalize(this);
        }

        private OtlpLog4NetAppender BuildAppender() =>
            new(_loggerFactory.Object, _enricher.Object, _options.Object);

        private static LoggingEvent BuildEvent(Level level, string message, Exception? ex = null)
        {
            var data = new LoggingEventData
            {
                Level = level,
                Message = message,
                LoggerName = "TestLogger",
                TimeStampUtc = DateTime.UtcNow,
                ExceptionString = ex?.Message,
            };
            return new LoggingEvent(data);
        }

        // Data-constructed events are already Fix=All with a null MessageObject, so they cannot reach
        // the rendering, re-fix or exception-object paths. Those need a live event.
        private static LoggingEvent BuildLiveEvent(Level level, object message, Exception? ex = null) =>
            new(typeof(OtlpLog4NetAppenderTests), Repository, "TestLogger", level, message, ex);

        private static log4net.Repository.ILoggerRepository Repository =>
            log4net.LogManager.GetLogger(typeof(OtlpLog4NetAppenderTests)).Logger.Repository!;

        [Fact]
        public void Append_InfoLevel_LogsAtInformation()
        {
            var appender = BuildAppender();
            appender.DoAppend(BuildEvent(Level.Info, "info message"));
            _logger.Verify(l => l.Log(LogLevel.Information,
                It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                null, It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        }

        [Fact]
        public void Append_WarnLevel_LogsAtWarning()
        {
            var appender = BuildAppender();
            appender.DoAppend(BuildEvent(Level.Warn, "warn message"));
            _logger.Verify(l => l.Log(LogLevel.Warning,
                It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                null, It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        }

        [Fact]
        public void Append_ErrorLevel_LogsAtError()
        {
            var appender = BuildAppender();
            appender.DoAppend(BuildEvent(Level.Error, "error message"));
            _logger.Verify(l => l.Log(LogLevel.Error,
                It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                null, It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        }

        [Fact]
        public void Append_DebugLevel_LogsAtDebug()
        {
            var appender = BuildAppender();
            appender.DoAppend(BuildEvent(Level.Debug, "debug message"));
            _logger.Verify(l => l.Log(LogLevel.Debug,
                It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                null, It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        }

        [Fact]
        public void Append_TraceLevel_LogsAtTrace()
        {
            var appender = BuildAppender();
            appender.DoAppend(BuildEvent(Level.Trace, "trace message"));
            _logger.Verify(l => l.Log(LogLevel.Trace,
                It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                null, It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        }

        [Fact]
        public void Append_FatalLevel_LogsAtError()
        {
            var appender = BuildAppender();
            appender.DoAppend(BuildEvent(Level.Fatal, "fatal message"));
            _logger.Verify(l => l.Log(LogLevel.Error,
                It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                null, It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        }

        [Fact]
        public void Append_ShortMessage_SingleChunk()
        {
            _options.Setup(o => o.MaxLogMessageLength).Returns(1000);
            var appender = BuildAppender();
            appender.DoAppend(BuildEvent(Level.Info, "short"));
            _logger.Verify(l => l.Log(It.IsAny<LogLevel>(),
                It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                null, It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        }

        [Fact]
        public void Append_LongMessage_MultipleChunks()
        {
            _options.Setup(o => o.MaxLogMessageLength).Returns(5);
            var appender = BuildAppender();
            appender.DoAppend(BuildEvent(Level.Info, "0123456789abcde")); // 15 chars → 3 chunks
            _logger.Verify(l => l.Log(It.IsAny<LogLevel>(),
                It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                null, It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Exactly(3));
        }

        [Fact]
        public void Append_WithException_PassesExceptionOnLastChunk()
        {
            _options.Setup(o => o.MaxLogMessageLength).Returns(5);
            var appender = BuildAppender();
            var ex = new InvalidOperationException("failure");

            appender.DoAppend(BuildLiveEvent(Level.Error, "0123456789", ex));

            // Two chunks: the exception rides the last one only.
            _logger.Verify(l => l.Log(LogLevel.Error,
                It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                ex, It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
            _logger.Verify(l => l.Log(LogLevel.Error,
                It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                null, It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        }

        [Fact]
        public void Append_FixedEventWithExceptionString_AddsExceptionToState()
        {
            var capture = SetupLoggerStateCapture();
            var appender = BuildAppender();

            // A buffered/forwarded event: ExceptionObject is null, only the string survives.
            var logEvent = new LoggingEvent(new LoggingEventData
            {
                Level = Level.Error,
                Message = "payment failed",
                LoggerName = "TestLogger",
                TimeStampUtc = DateTime.UtcNow,
                ExceptionString = "System.InvalidOperationException: boom",
            });
            Assert.Null(logEvent.ExceptionObject);

            appender.DoAppend(logEvent);

            Assert.NotNull(capture.State);
            Assert.Equal("System.InvalidOperationException: boom", capture.State!["exception"]);
        }

        [Fact]
        public void Append_EnricherCalled()
        {
            var appender = BuildAppender();
            appender.DoAppend(BuildEvent(Level.Info, "test"));
            _enricher.Verify(e => e.Enrich(null), Times.Once);
        }

        private LogCapture SetupLoggerStateCapture()
        {
            var capture = new LogCapture();
            _logger.Setup(l => l.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(), null, It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
                .Callback<LogLevel, EventId, object, Exception?, Delegate>(
                    (lvl, id, state, ex, fmt) => capture.State = state as Dictionary<string, object>);
            return capture;
        }

        [Fact]
        public void Append_LaterChunks_CarryOnlyShortPropertiesAndShareAChunkId()
        {
            var states = SetupLoggerStatesCapture();
            var loggingEvent = BuildEvent(Level.Info, new string('m', 2500));
            loggingEvent.Properties["Small"] = "x";
            loggingEvent.Properties["Big"] = new string('b', NewRelicLogLimits.ChunkContextValueLength + 1);

            BuildAppender().DoAppend(loggingEvent);

            Assert.Equal(3, states.Count);
            Assert.True(states[0].ContainsKey("property.Big"));
            Assert.All(states.Skip(1), s => Assert.False(s.ContainsKey("property.Big")));
            Assert.All(states, s => Assert.Equal("x", s["property.Small"]));
            Assert.Single(states.Select(s => s["chunk.id"]).Distinct());
        }

        [Fact]
        public void Append_PropertyLongerThanAChunk_IsCutToTheAttributeLength()
        {
            var capture = SetupLoggerStateCapture();
            _options.Setup(o => o.MaxAttributeValueLength).Returns(100);
            var loggingEvent = BuildEvent(Level.Info, "short");
            loggingEvent.Properties["Huge"] = new string('h', 5000);

            BuildAppender().DoAppend(loggingEvent);

            Assert.Equal(100, ((string)capture.State!["property.Huge"]).Length);
        }

        [Fact]
        public void FitJson_WithinBudget_LeavesTheMessageForNewRelicToExpand()
        {
            var state = new Dictionary<string, object> { ["level"] = "INFO" };

            Assert.Equal(JsonObject(10), OtlpLog4NetAppender.FitJson(state, JsonObject(10), 100));
        }

        [Fact]
        public void FitJson_JsonMessageOverBudget_IsPrefixed()
        {
            var state = new Dictionary<string, object> { ["level"] = "INFO" };

            Assert.Equal(NewRelicLogLimits.JsonMessagePrefix + JsonObject(150), OtlpLog4NetAppender.FitJson(state, JsonObject(150), 100));
        }

        [Fact]
        public void FitJson_JsonPropertiesOverBudget_ArePrefixedLargestFirst()
        {
            var state = new Dictionary<string, object>
            {
                ["property.Big"] = JsonObject(150),
                ["property.Small"] = JsonObject(20),
            };

            var message = OtlpLog4NetAppender.FitJson(state, "plain", 100);

            Assert.Equal("plain", message);
            Assert.Equal(NewRelicLogLimits.JsonMessagePrefix + JsonObject(150), state["property.Big"]);
            Assert.Equal(JsonObject(20), state["property.Small"]);
        }

        [Fact]
        public void Append_JsonMessageOverBudget_IsSentAsPrefixedText()
        {
            var capture = SetupLoggerStateCapture();
            _options.Setup(o => o.MaxLogMessageLength).Returns(100_000);

            BuildAppender().DoAppend(BuildEvent(Level.Info, JsonObject(300)));

            Assert.Equal(NewRelicLogLimits.JsonMessagePrefix + JsonObject(300), capture.State!["message"]);
        }

        private static string JsonObject(int fields) =>
            "{" + string.Join(",", Enumerable.Range(0, fields).Select(i => $"\"j{i}\":{i}")) + "}";

        // A report that export is failing must not depend on that export, or feed back into it.
        [Fact]
        public void Append_TelemetryDiagnosticEvent_IsNotExported()
        {
            var capture = SetupLoggerStateCapture();
            var loggingEvent = BuildEvent(Level.Warn, "export failing");
            loggingEvent.Properties[TelemetryDiagnostics.MarkerProperty] = true;

            BuildAppender().DoAppend(loggingEvent);

            Assert.Null(capture.State);
        }

        [Fact]
        public void TelemetryDiagnostics_WritesMarkedEventsThatTheAppenderSkips()
        {
            var repository = log4net.LogManager.CreateRepository("diag-" + Guid.NewGuid().ToString("N"));
            var memory = new log4net.Appender.MemoryAppender();
            var capture = SetupLoggerStateCapture();
            log4net.Config.BasicConfigurator.Configure(repository, memory, BuildAppender());
            var log = log4net.LogManager.GetLogger(repository.Name, "telemetry");

            Log4NetTelemetryDiagnostics.Write(log, new TelemetryProblem("OpenTelemetry-Sdk", "ExportFailed", "timed out", isError: true, suppressed: 2));

            var written = Assert.Single(memory.GetEvents());
            Assert.Equal(Level.Error, written.Level);
            Assert.True(written.LookupProperty(TelemetryDiagnostics.MarkerProperty) as bool?);
            Assert.Equal("OpenTelemetry-Sdk", written.LookupProperty("TelemetrySource"));
            Assert.Contains("timed out", written.RenderedMessage);
            Assert.Null(capture.State);
        }

        [Fact]
        public void StartTelemetryDiagnostics_NullLog_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => ((log4net.ILog)null!).StartTelemetryDiagnostics());
        }

        [Fact]
        public void Append_StateContainsMessageAndLevel()
        {
            var capture = SetupLoggerStateCapture();

            var appender = BuildAppender();
            appender.DoAppend(BuildEvent(Level.Info, "hello world"));

            Assert.NotNull(capture.State);
            Assert.Equal("hello world", capture.State!["message"]);
            Assert.Equal("INFO", capture.State["level"]);
            Assert.Equal("TestLogger", capture.State["logger.name"]);
        }

        [Fact]
        public void Constructor_WithCategoryName_UsesIt()
        {
            var appender = new OtlpLog4NetAppender(
                _loggerFactory.Object, _enricher.Object, _options.Object, "MyCategory");
            _loggerFactory.Verify(f => f.CreateLogger("MyCategory"), Times.Once);
        }

        [Fact]
        public void Constructor_NoCategoryName_UsesTypeName()
        {
            BuildAppender();
            _loggerFactory.Verify(f => f.CreateLogger(
                It.Is<string>(s => s.Contains("OtlpLog4NetAppender"))), Times.Once);
        }

        [Fact]
        public void Append_WhenDisabled_DoesNotCallEnricher()
        {
            _options.Setup(o => o.Enabled).Returns(false);
            var appender = BuildAppender();
            appender.DoAppend(BuildEvent(Level.Info, "test"));
            _enricher.Verify(e => e.Enrich(null), Times.Never);
        }

        [Fact]
        public void Append_EnricherThrows_DoesNotPropagate()
        {
            _options.Setup(o => o.Enabled).Returns(true);
            _enricher.Setup(e => e.Enrich(null)).Throws(new InvalidOperationException("enricher fail"));
            var appender = BuildAppender();
            var exception = Record.Exception(() => appender.DoAppend(BuildEvent(Level.Info, "test")));
            Assert.Null(exception);
        }

        [Fact]
        public void Append_NullMessage_DoesNotThrow()
        {
            _options.Setup(o => o.Enabled).Returns(true);
            var appender = BuildAppender();
            var data = new LoggingEventData
            {
                Level = Level.Info,
                Message = null,
                LoggerName = "TestLogger",
                TimeStampUtc = DateTime.UtcNow,
            };
            var logEvent = new LoggingEvent(data);
            var exception = Record.Exception(() => appender.DoAppend(logEvent));
            Assert.Null(exception);
        }

        [Fact]
        public void Append_WithMessageObject_IncludesSerializedObject()
        {
            var capture = SetupLoggerStateCapture();
            var appender = BuildAppender();
            var logEvent = new LoggingEvent(typeof(OtlpLog4NetAppenderTests), null,
                "TestLogger", Level.Info, new { Payload = "data" }, null);
            appender.DoAppend(logEvent);

            Assert.NotNull(capture.State);
            Assert.True(capture.State!.ContainsKey("messageObject"));
            Assert.Contains("Payload", capture.State["messageObject"]?.ToString() ?? "");
        }

        [Fact]
        public void Append_WithIdentityAndDomain_AddsToState()
        {
            var capture = SetupLoggerStateCapture();
            var appender = BuildAppender();
            appender.DoAppend(new LoggingEvent(new LoggingEventData
            {
                Level = Level.Info,
                Message = "test",
                LoggerName = "TestLogger",
                TimeStampUtc = DateTime.UtcNow,
                Identity = "testuser",
                Domain = "TESTDOMAIN",
            }));

            Assert.NotNull(capture.State);
            Assert.Equal("testuser", capture.State!["user.identity"]);
            Assert.Equal("TESTDOMAIN", capture.State["app.domain"]);
        }

        [Fact]
        public void Append_WithLog4NetProperties_AddsPrefixedEntries()
        {
            var capture = SetupLoggerStateCapture();
            var appender = BuildAppender();
            var logEvent = new LoggingEvent(typeof(OtlpLog4NetAppenderTests),
                log4net.LogManager.GetLogger(typeof(OtlpLog4NetAppenderTests)).Logger.Repository,
                "TestLogger", Level.Info, "test with props", null);
            logEvent.Properties["RequestId"] = "req-123";
            appender.DoAppend(logEvent);

            Assert.NotNull(capture.State);
            Assert.True(capture.State!.ContainsKey("property.RequestId"));
            Assert.Equal("req-123", capture.State["property.RequestId"]);
        }

        [Fact]
        public void Append_EnricherReturnsNull_DoesNotThrow()
        {
            _enricher.Setup(e => e.Enrich(null)).Returns((Dictionary<string, object>)null!);
            var appender = BuildAppender();
            var exception = Record.Exception(() => appender.DoAppend(BuildEvent(Level.Info, "test")));
            Assert.Null(exception);
        }

        [Fact]
        public void Append_MessageObjectSerializationFails_StillEmitsLog()
        {
            var capture = SetupLoggerStateCapture();
            var appender = BuildAppender();
            var logEvent = new LoggingEvent(typeof(OtlpLog4NetAppenderTests), null,
                "TestLogger", Level.Info, new ThrowingMessageObject(), null);
            var exception = Record.Exception(() => appender.DoAppend(logEvent));
            Assert.Null(exception);
            Assert.NotNull(capture.State);
            // Fallback to ToString() should be used
            Assert.True(capture.State!.ContainsKey("messageObject"));
        }

        [Fact]
        public void Append_CyclicMessageObject_SerializesWithoutThrowing()
        {
            var capture = SetupLoggerStateCapture();
            var node = new CyclicNode { Name = "root" };
            node.Self = node;
            BuildAppender().DoAppend(new LoggingEvent(typeof(OtlpLog4NetAppenderTests), null,
                "TestLogger", Level.Info, node, null));

            Assert.Contains("root", capture.State!["messageObject"]?.ToString() ?? "");
        }

        [Fact]
        public void Append_EntityWithRelationshipsMessageObject_NeverInvokesGetters()
        {
            var capture = SetupLoggerStateCapture();
            BuildAppender().DoAppend(new LoggingEvent(typeof(OtlpLog4NetAppenderTests), null,
                "TestLogger", Level.Info, new FakeEntity(), null));

            Assert.Equal(typeof(FakeEntity).FullName, capture.State!["messageObject"]?.ToString());
        }

        [Fact]
        public void Append_MessageObjectHoldingEntity_SkipsEntityMember()
        {
            var capture = SetupLoggerStateCapture();
            BuildAppender().DoAppend(new LoggingEvent(typeof(OtlpLog4NetAppenderTests), null,
                "TestLogger", Level.Info, new EntityHolder(), null));

            var json = capture.State!["messageObject"]?.ToString() ?? "";
            Assert.Contains("Label", json);
            Assert.DoesNotContain("Entity", json);
        }

        [Fact]
        public void Append_QueryMessageObject_IsNotEnumerated()
        {
            var capture = SetupLoggerStateCapture();
            var query = new CountingQuery<int>();

            BuildAppender().DoAppend(new LoggingEvent(typeof(OtlpLog4NetAppenderTests), null,
                "TestLogger", Level.Info, query, null));

            Assert.Equal(typeof(CountingQuery<int>).FullName, capture.State!["messageObject"]?.ToString());
            Assert.Equal(0, query.Enumerations);
        }

        // The same EF match the span renderer uses: a context in the host's own namespace is caught
        // through its base type, and a set or query member is dropped without being enumerated.
        [Fact]
        public void Append_MessageObjectHoldingContextSetAndQuery_SkipsThem()
        {
            var capture = SetupLoggerStateCapture();
            var service = new ServiceHoldingData();

            BuildAppender().DoAppend(new LoggingEvent(typeof(OtlpLog4NetAppenderTests), null,
                "TestLogger", Level.Info, service, null));

            var json = capture.State!["messageObject"]?.ToString() ?? "";
            Assert.Contains("svc", json);
            Assert.DoesNotContain("Db", json);
            Assert.DoesNotContain("Orders", json);
            Assert.DoesNotContain("Pending", json);
            Assert.Equal(0, service.Orders.Enumerations);
            Assert.Equal(0, service.Db.Orders.Enumerations);
            Assert.Equal(0, service.Pending.Enumerations);
        }

        [Fact]
        public void Append_EntityFrameworkCoreContextMessageObject_NeverInvokesGetters()
        {
            var capture = SetupLoggerStateCapture();
            var context = new OrdersCoreContext();

            BuildAppender().DoAppend(new LoggingEvent(typeof(OtlpLog4NetAppenderTests), null,
                "TestLogger", Level.Info, context, null));

            Assert.Equal(typeof(OrdersCoreContext).FullName, capture.State!["messageObject"]?.ToString());
            Assert.Equal(0, context.Reads());
        }

        [Fact]
        public void Append_MaxLogMessageLengthZero_DoesNotThrow()
        {
            _options.Setup(o => o.MaxLogMessageLength).Returns(0);
            var appender = BuildAppender();
            var exception = Record.Exception(() => appender.DoAppend(BuildEvent(Level.Info, "some message")));
            Assert.Null(exception);
            _logger.Verify(l => l.Log(It.IsAny<LogLevel>(),
                It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                null, It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        }

        [Fact]
        public void Append_ChunkEmitFails_RemainingChunksStillAttempted()
        {
            _options.Setup(o => o.MaxLogMessageLength).Returns(5);
            var callCount = 0;
            _logger.Setup(l => l.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(), null, It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
                .Callback<LogLevel, EventId, object, Exception?, Delegate>((lvl, id, state, ex, fmt) =>
                {
                    callCount++;
                    if (callCount == 1) throw new InvalidOperationException("chunk fail");
                });
            var appender = BuildAppender();
            // 15 chars / 5 = 3 chunks; first throws, remaining should still be attempted
            var exception = Record.Exception(() => appender.DoAppend(BuildEvent(Level.Info, "0123456789abcde")));
            Assert.Null(exception);
            Assert.True(callCount >= 2, $"Expected at least 2 Log calls, got {callCount}");
        }

        [Fact]
        public void Append_WithRealLogger_FormatterIsInvoked()
        {
            var messages = new List<string>();
            using var factory = LoggerFactory.Create(b => b.AddProvider(new CapturingLoggerProvider(messages)));
            var appender = new OtlpLog4NetAppender(factory, _enricher.Object, _options.Object);
            appender.DoAppend(BuildEvent(Level.Info, "real logger test"));
            Assert.True(messages.Count > 0);
            Assert.Contains("real logger test", messages[0]);
        }

        private List<Dictionary<string, object>> SetupLoggerStatesCapture()
        {
            var states = new List<Dictionary<string, object>>();
            _logger.Setup(l => l.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
                .Callback<LogLevel, EventId, object, Exception?, Delegate>(
                    (lvl, id, state, ex, fmt) =>
                    {
                        if (state is Dictionary<string, object> dict)
                            states.Add(new Dictionary<string, object>(dict));
                    });
            return states;
        }

        [Fact]
        public void Append_FixNoneEvent_StillExportsMessageAndContext()
        {
            var capture = SetupLoggerStateCapture();
            var appender = BuildAppender();

            // The original defect: a repository configured with Fix="0" blanks RenderedMessage and
            // every volatile field, and the appender exported nothing at all.
            var logEvent = BuildLiveEvent(Level.Info, "fix-none message");
            logEvent.Fix = FixFlags.None;
            Assert.True(string.IsNullOrEmpty(logEvent.RenderedMessage));

            appender.DoAppend(logEvent);

            Assert.NotNull(capture.State);
            Assert.Equal("fix-none message", capture.State!["message"]);
            Assert.True(capture.State.ContainsKey("thread.name"));
        }

        [Fact]
        public void Append_CollectionMessageObject_RendersViaRendererMap()
        {
            var capture = SetupLoggerStateCapture();
            var appender = BuildAppender();

            var list = new System.Collections.ArrayList { "x", "y" };
            appender.DoAppend(BuildLiveEvent(Level.Info, list));

            Assert.NotNull(capture.State);
            Assert.Equal("{x, y}", capture.State!["message"]);
        }

        [Fact]
        public void Append_EmptyMessage_StillEmits()
        {
            var capture = SetupLoggerStateCapture();
            var appender = BuildAppender();

            // Matches ChunkingOpenTelemetrySink, which exports empty-bodied events rather than dropping
            // them along with their correlation id and properties.
            appender.DoAppend(new LoggingEvent(new LoggingEventData
            {
                Level = Level.Info,
                Message = null,
                LoggerName = "TestLogger",
                TimeStampUtc = DateTime.UtcNow,
            }));

            _logger.Verify(l => l.Log(It.IsAny<LogLevel>(),
                It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                null, It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
            Assert.NotNull(capture.State);
            Assert.Equal(string.Empty, capture.State!["message"]);
            Assert.Equal("TestLogger", capture.State["logger.name"]);
        }

        [Fact]
        public void Append_ChunkedMessage_OmitsMessageObjectRatherThanExceedingTheCap()
        {
            _options.Setup(o => o.MaxLogMessageLength).Returns(5);
            var states = SetupLoggerStatesCapture();
            var appender = BuildAppender();

            appender.DoAppend(BuildLiveEvent(Level.Info, new { Payload = "0123456789abcde" }));

            // A chunked message fills the cap by definition, leaving no budget on the same record for
            // messageObject. Dropping it is what keeps the record within the limit.
            Assert.True(states.Count > 1, $"Expected multiple chunks, got {states.Count}");
            Assert.All(states, s => Assert.False(s.ContainsKey("messageObject")));
        }

        [Fact]
        public void Append_ShortMessage_CarriesMessageObjectWithinRemainingBudget()
        {
            _options.Setup(o => o.MaxLogMessageLength).Returns(500);
            var capture = SetupLoggerStateCapture();
            var appender = BuildAppender();

            appender.DoAppend(BuildLiveEvent(Level.Info, new { Payload = "small" }));

            Assert.NotNull(capture.State);
            var message = (string)capture.State!["message"];
            var messageObject = (string)capture.State["messageObject"];
            Assert.True(message.Length + messageObject.Length <= 500,
                $"combined record {message.Length + messageObject.Length} exceeds the cap");
        }

        [Fact]
        public void Append_LiveException_IsNotDuplicatedIntoState()
        {
            // Plural helper on purpose: the singular SetupLoggerStateCapture matches only a null
            // exception argument, so it would never fire for this call.
            var states = SetupLoggerStatesCapture();
            var appender = BuildAppender();
            var ex = new InvalidOperationException("boom");

            appender.DoAppend(BuildLiveEvent(Level.Error, "payment failed", ex));

            // The exception is passed to ILogger.Log, which the exporter expands into exception.*;
            // repeating the whole stack trace in state would double the record size.
            var state = Assert.Single(states);
            Assert.False(state.ContainsKey("exception"));
            _logger.Verify(l => l.Log(LogLevel.Error,
                It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                ex, It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        }

        [Fact]
        public void Append_UnfixedLiveEvent_IsNotMutated()
        {
            var appender = BuildAppender();
            var logEvent = BuildLiveEvent(Level.Info, "lazily rendered");

            appender.DoAppend(logEvent);

            // Fix mutates the event log4net shares with every other appender, so the healthy path
            // must leave it alone; the lazy properties already return correct values.
            Assert.Equal(FixFlags.None, logEvent.Fix);
        }

        [Fact]
        public void Append_EmptyMessageLiveEvent_IsNotFixed()
        {
            var appender = BuildAppender();
            var logEvent = BuildLiveEvent(Level.Info, string.Empty);

            appender.DoAppend(logEvent);

            // Nothing to recover, so re-fixing would only pay for a stack walk and mutate the shared event.
            Assert.Equal(FixFlags.None, logEvent.Fix);
        }

        [Theory]
        [InlineData("correlation.id")]  // an enricher key
        [InlineData("message")]         // a key this appender always writes
        [InlineData("overflow")]        // a key it writes only on chunked events
        public void Append_PropertyNamedAfterAnyReservedKey_IsStillExported(string propertyName)
        {
            _enricher.Setup(e => e.Enrich(null)).Returns(() =>
                new Dictionary<string, object> { ["correlation.id"] = "from-enricher" });
            var capture = SetupLoggerStateCapture();
            var appender = BuildAppender();

            var logEvent = BuildLiveEvent(Level.Info, "short");
            logEvent.Properties[propertyName] = "user-value";
            appender.DoAppend(logEvent);

            // Properties land under a property. prefix, so no name can collide with an attribute the
            // appender or enricher writes; filtering on the bare name could only lose data.
            Assert.NotNull(capture.State);
            Assert.Equal("user-value", capture.State![$"property.{propertyName}"]);
        }

        [Fact]
        public void Append_ExceptionFreeLiveEvent_DoesNotCacheExceptionString()
        {
            var appender = BuildAppender();
            var logEvent = BuildLiveEvent(Level.Info, "no exception here");

            appender.DoAppend(logEvent);

            // GetExceptionString() caches "" into the event's data, mutating state log4net shares with
            // every downstream appender, so it must not run for an event that has no exception. Only
            // observable through log4net internals; skipped rather than failed if they are renamed.
            var dataField = typeof(LoggingEvent).GetField("_data",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var data = dataField?.GetValue(logEvent);
            var exceptionString = data?.GetType().GetField("ExceptionString")?.GetValue(data);

            Assert.True(data == null || exceptionString == null,
                "GetExceptionString() ran and cached an empty string onto the shared event");
        }

        [Fact]
        public void Append_PropertyNamedAfterAppenderKey_IsStillExportedOnEveryChunk()
        {
            _options.Setup(o => o.MaxLogMessageLength).Returns(5);
            var states = SetupLoggerStatesCapture();
            var appender = BuildAppender();

            var logEvent = BuildLiveEvent(Level.Info, new { Payload = "0123456789abcde" });
            logEvent.Properties["messageObject"] = "user-value";
            appender.DoAppend(logEvent);

            // property.messageObject cannot collide with messageObject, so it must survive - and appear
            // identically on every chunk, since suppression is what made chunks disagree before.
            Assert.True(states.Count > 1, $"Expected multiple chunks, got {states.Count}");
            Assert.All(states, s => Assert.Equal("user-value", s["property.messageObject"]));
        }

        [Fact]
        public void Append_OversizedMessageObject_IsBoundedByMaxLogMessageLength()
        {
            _options.Setup(o => o.MaxLogMessageLength).Returns(50);
            var states = SetupLoggerStatesCapture();
            var appender = BuildAppender();

            appender.DoAppend(BuildLiveEvent(Level.Info, new { Payload = new string('x', 400) }));

            // Every emitted record must stay within the cap, message plus messageObject together.
            Assert.NotEmpty(states);
            Assert.All(states, s =>
            {
                var total = ((string)s["message"]).Length
                        + (s.TryGetValue("messageObject", out var mo) ? ((string)mo).Length : 0);
                Assert.True(total <= 50, $"record of {total} exceeds the cap");
            });
        }

        [Fact]
        public void Append_StringMessageObject_DoesNotDuplicateIntoState()
        {
            var capture = SetupLoggerStateCapture();
            var appender = BuildAppender();

            appender.DoAppend(BuildLiveEvent(Level.Info, "plain string"));

            Assert.NotNull(capture.State);
            Assert.False(capture.State!.ContainsKey("messageObject"));
        }

        [Fact]
        public void Append_NoLevelOrLoggerName_LogsAtInformationWithEmptyValues()
        {
            var capture = SetupLoggerStateCapture();

            BuildAppender().DoAppend(new LoggingEvent(new LoggingEventData
            {
                Message = "no level",
                TimeStampUtc = DateTime.UtcNow,
            }));

            _logger.Verify(l => l.Log(LogLevel.Information,
                It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                null, It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
            Assert.Equal(string.Empty, capture.State!["level"]);
            Assert.Equal(string.Empty, capture.State["logger.name"]);
        }

        [Fact]
        public void Append_LocationAndThread_AreAddedToState()
        {
            var capture = SetupLoggerStateCapture();

            BuildAppender().DoAppend(new LoggingEvent(new LoggingEventData
            {
                Level = Level.Info,
                Message = "located",
                LoggerName = "TestLogger",
                ThreadName = "worker-1",
                TimeStampUtc = DateTime.UtcNow,
                LocationInfo = new LocationInfo("Orders.Service", "Place", "Service.cs", "42"),
            }));

            Assert.Equal("worker-1", capture.State!["thread.name"]);
            Assert.Equal("Orders.Service", capture.State["source.class"]);
            Assert.Equal("Place", capture.State["source.method"]);
            Assert.Equal("Service.cs", capture.State["source.file"]);
            Assert.Equal("42", capture.State["source.line"]);
        }

        [Fact]
        public void Append_EmptyLocationThreadAndDomain_AddNothing()
        {
            var capture = SetupLoggerStateCapture();

            BuildAppender().DoAppend(new LoggingEvent(new LoggingEventData
            {
                Level = Level.Info,
                Message = "unlocated",
                LoggerName = "TestLogger",
                ThreadName = string.Empty,
                Domain = string.Empty,
                Identity = string.Empty,
                TimeStampUtc = DateTime.UtcNow,
                LocationInfo = new LocationInfo(string.Empty, string.Empty, string.Empty, string.Empty),
            }));

            Assert.DoesNotContain(capture.State!.Keys, k => k.StartsWith("source.") || k.StartsWith("property."));
            Assert.False(capture.State.ContainsKey("thread.name"));
            Assert.False(capture.State.ContainsKey("app.domain"));
            Assert.False(capture.State.ContainsKey("user.identity"));
        }

        [Fact]
        public void Append_NoLocation_AddsNoSourceAttributes()
        {
            var capture = SetupLoggerStateCapture();
            var logEvent = BuildEvent(Level.Info, "no location");
            // A fixed event stops caching, so an absent location stays absent instead of becoming "?".
            logEvent.Fix = FixFlags.None;
            Assert.Null(logEvent.LocationInformation);

            BuildAppender().DoAppend(logEvent);

            Assert.DoesNotContain(capture.State!.Keys, k => k.StartsWith("source."));
        }

        [Fact]
        public void Append_NullPropertyValue_IsExportedAsEmpty()
        {
            var capture = SetupLoggerStateCapture();
            var logEvent = BuildEvent(Level.Info, "with null property");
            logEvent.Properties["Nothing"] = null;

            BuildAppender().DoAppend(logEvent);

            Assert.Equal(string.Empty, capture.State!["property.Nothing"]);
        }

        [Fact]
        public void Append_FailureBeforeEmitting_IsReportedNotThrown()
        {
            _options.Setup(o => o.MaxLogMessageLength).Throws(new InvalidOperationException("options broken"));
            var appender = BuildAppender();
            var errors = new RecordingErrorHandler();
            appender.ErrorHandler = errors;

            var exception = Record.Exception(() => appender.DoAppend(BuildEvent(Level.Info, "doomed")));

            Assert.Null(exception);
            Assert.Contains(errors.Messages, m => m.Contains("error in Append"));
            _logger.Verify(l => l.Log(It.IsAny<LogLevel>(),
                It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Never);
        }

        [Fact]
        public void Append_FixThatThrows_IsReportedAndTheEventStillExported()
        {
            var capture = SetupLoggerStateCapture();
            var appender = BuildAppender();
            var errors = new RecordingErrorHandler();
            appender.ErrorHandler = errors;

            appender.DoAppend(new LoggingEvent(typeof(OtlpLog4NetAppenderTests), null, "TestLogger", Level.Info, new RenderingFailsOnce(), null));

            Assert.Contains(errors.Messages, m => m.Contains("could not fix event data"));
            Assert.Equal("recovered", capture.State!["message"]);
        }

        [Fact]
        public void Append_MessageObjectRenderingEmpty_IsReFixedAndExported()
        {
            var capture = SetupLoggerStateCapture();

            BuildAppender().DoAppend(BuildLiveEvent(Level.Info, new BlankRendering()));

            Assert.Equal(string.Empty, capture.State!["message"]);
            Assert.Equal("{\"Id\":7}", capture.State["messageObject"]);
        }

        [Fact]
        public void Append_MessageObjectOverTheRemainingBudget_IsCutToFit()
        {
            _options.Setup(o => o.MaxLogMessageLength).Returns(100);
            var capture = SetupLoggerStateCapture();

            BuildAppender().DoAppend(BuildLiveEvent(Level.Info, new ShortRenderingLargeBody()));

            Assert.Equal("short", capture.State!["message"]);
            var messageObject = (string)capture.State["messageObject"];
            Assert.Equal(95, messageObject.Length);
            Assert.StartsWith("{\"Body\":\"bbbb", messageObject);
        }

        [Fact]
        public void Append_MessageObjectWhoseSerializationAndToStringThrow_IsOmitted()
        {
            var capture = SetupLoggerStateCapture();
            var logEvent = new PreRenderedEvent("unserializable", new ThrowingEverywhere());

            BuildAppender().DoAppend(logEvent);

            Assert.Equal("unserializable", capture.State!["message"]);
            Assert.False(capture.State.ContainsKey("messageObject"));
        }

        [Fact]
        public void Append_EntityBehindAnObjectTypedMember_IsWrittenWithoutItsMembers()
        {
            var capture = SetupLoggerStateCapture();

            BuildAppender().DoAppend(BuildLiveEvent(Level.Info, new LooselyTypedHolder()));

            Assert.Equal("{\"Label\":\"x\",\"Item\":{}}", capture.State!["messageObject"]);
        }

        [Fact]
        public void Append_CollectionOfEntitiesMember_IsSkippedOtherCollectionsAreKept()
        {
            var capture = SetupLoggerStateCapture();

            BuildAppender().DoAppend(BuildLiveEvent(Level.Info, new CollectionHolder()));

            Assert.Equal("{\"Tags\":[\"a\"]}", capture.State!["messageObject"]);
        }

        [Fact]
        public void AttributeBudget_OptionsWithoutAConfiguredLimit_UseTheDefaultLessReservedAttributes()
        {
            var options = new Mock<IOpenTelemetryOptions>();
            options.Setup(o => o.Attributes).Returns((Dictionary<string, object>)null!);

            Assert.Equal(OpenTelemetryOptionsBase.DefaultLogsMaxAttributes - 24, OtlpLog4NetAppender.AttributeBudget(options.Object));
        }

        [Fact]
        public void AttributeBudget_ConfiguredLimit_IsReducedByResourceAttributes()
        {
            var options = new TestOptions { LogsMaxAttributes = 100 };

            Assert.Equal(100 - 24 - options.Attributes.Count, OtlpLog4NetAppender.AttributeBudget(options));
        }

        [Fact]
        public void AttributeBudget_NonPositiveLimit_FallsBackToTheDefault()
        {
            var options = new TestOptions { LogsMaxAttributes = 0 };

            Assert.Equal(OpenTelemetryOptionsBase.DefaultLogsMaxAttributes - 24 - options.Attributes.Count,
                OtlpLog4NetAppender.AttributeBudget(options));
        }

        [Fact]
        public void AttributeBudget_NeverFallsBelowOne()
        {
            var options = new TestOptions { LogsMaxAttributes = 5 };

            Assert.Equal(1, OtlpLog4NetAppender.AttributeBudget(options));
        }

        private sealed class RecordingErrorHandler : log4net.Core.IErrorHandler
        {
            public List<string> Messages { get; } = [];

            public void Error(string message, Exception? e, ErrorCode errorCode) => Messages.Add(message);

            public void Error(string message, Exception? e) => Messages.Add(message);

            public void Error(string message) => Messages.Add(message);
        }

        // Without a repository log4net renders with ToString and caches nothing for null, so the fix
        // renders again and that is where it fails.
        private sealed class RenderingFailsOnce
        {
            private int _calls;

            public override string? ToString() => ++_calls switch
            {
                1 => null,
                2 => throw new InvalidOperationException("render failed"),
                _ => "recovered",
            };
        }

        private sealed class BlankRendering
        {
            [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "The appender reads instance properties by reflection; a static one would not be serialized.")]
            public int Id => 7;

            public override string ToString() => string.Empty;
        }

        private sealed class ShortRenderingLargeBody
        {
            [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "The appender reads instance properties by reflection; a static one would not be serialized.")]
            public string Body => new('b', 400);

            public override string ToString() => "short";
        }

        // A forwarded event: the message arrives rendered, with the original object still attached.
        private sealed class PreRenderedEvent : LoggingEvent
        {
            public PreRenderedEvent(string rendered, object messageObject)
                : base(new LoggingEventData { Level = Level.Info, Message = rendered, LoggerName = "TestLogger", TimeStampUtc = DateTime.UtcNow })
            {
                MessageObject = messageObject;
            }
        }

        private sealed class ThrowingEverywhere
        {
            [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "The appender reads instance properties by reflection; a static one would not be serialized.")]
            public string Value => throw new InvalidOperationException("getter");

            public override string ToString() => throw new InvalidOperationException("ToString");
        }

        private sealed class LooselyTypedHolder
        {
            [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "The appender reads instance properties by reflection; a static one would not be serialized.")]
            public string Label => "x";

            [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "The appender reads instance properties by reflection; a static one would not be serialized.")]
            public object Item => new FakeEntity();
        }

        private sealed class CollectionHolder
        {
            [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "The appender reads instance properties by reflection; a static one would not be serialized.")]
            public List<FakeEntity> Entities => throw new InvalidOperationException("getter invoked");

            [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "The appender reads instance properties by reflection; a static one would not be serialized.")]
            public List<string> Tags => ["a"];
        }
    }

    internal sealed class LogCapture
    {
        public Dictionary<string, object>? State { get; set; }
    }

    internal sealed class CapturingLoggerProvider(List<string> messages) : Microsoft.Extensions.Logging.ILoggerProvider
    {
        private readonly List<string> _messages = messages;

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);
        public void Dispose() { }
    }

    internal sealed class CapturingLogger(List<string> messages) : Microsoft.Extensions.Logging.ILogger
    {
        private readonly List<string> _messages = messages;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
            EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _messages.Add(formatter(state, exception));
        }
    }

    internal sealed class CyclicNode
    {
        public string Name { get; set; } = "";
        public CyclicNode? Self { get; set; }
    }

    internal interface IEntityWithRelationships
    {
    }

    internal sealed class FakeEntity : IEntityWithRelationships
    {
        [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "The appender reads instance properties by reflection; a static one would not be serialized.")]
        public string Name => throw new InvalidOperationException("getter invoked");
    }

    internal sealed class EntityHolder
    {
        public string Label { get; set; } = "x";
        [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "The appender reads instance properties by reflection; a static one would not be serialized.")]
        public FakeEntity Entity => throw new InvalidOperationException("getter invoked");
    }

    internal sealed class ThrowingMessageObject
    {
        // Instance property required for JsonSerializer to discover and invoke it
        public string Name { get; set; } = "test";
        public string Value => throw new InvalidOperationException($"cannot serialize {Name}");

        public override string ToString() => "ThrowingMessageObject-fallback";
    }
}
