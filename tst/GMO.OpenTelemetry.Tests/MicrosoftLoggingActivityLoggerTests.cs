using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class MicrosoftLoggingActivityLoggerTests
    {
        private readonly Mock<ILogger> _logger = new();

        [Fact]
        public void Constructor_NullLogger_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(() =>
                new MicrosoftLoggingActivityLogger(null!));
        }

        [Fact]
        public void LogActivityStart_LogsAtConfiguredLevel()
        {
            _logger.Setup(l => l.IsEnabled(LogLevel.Information)).Returns(true);
            var sut = new MicrosoftLoggingActivityLogger(_logger.Object);
            sut.LogActivityStart("MyOp");
            _logger.Verify(
                l => l.Log(LogLevel.Information, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                    null, It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public void LogActivityEnd_LogsAtConfiguredLevel()
        {
            _logger.Setup(l => l.IsEnabled(LogLevel.Information)).Returns(true);
            var sut = new MicrosoftLoggingActivityLogger(_logger.Object);
            sut.LogActivityEnd("MyOp", "Ok");
            _logger.Verify(
                l => l.Log(LogLevel.Information, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                    null, It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public void LogException_LogsAtErrorLevel()
        {
            _logger.Setup(l => l.IsEnabled(LogLevel.Error)).Returns(true);
            var sut = new MicrosoftLoggingActivityLogger(_logger.Object);
            var ex = new System.Exception("fail");
            sut.LogException("MyOp", ex);
            _logger.Verify(
                l => l.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                    ex, It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public void LogActivityStart_UsesDebugLevel_WhenConfigured()
        {
            _logger.Setup(l => l.IsEnabled(LogLevel.Debug)).Returns(true);
            var sut = new MicrosoftLoggingActivityLogger(_logger.Object, LogLevel.Debug);
            sut.LogActivityStart("MyOp");
            _logger.Verify(
                l => l.Log(LogLevel.Debug, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                    null, It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public void AllMethods_LoggerThrowsFromLog_AreSwallowed()
        {
            _logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            _logger.Setup(l => l.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
                .Throws(new InvalidOperationException("sink down"));
            var sut = new MicrosoftLoggingActivityLogger(_logger.Object);

            Assert.Null(Record.Exception(() => sut.LogActivityStart("MyOp")));
            Assert.Null(Record.Exception(() => sut.LogActivityEnd("MyOp", "Ok")));
            Assert.Null(Record.Exception(() => sut.LogException("MyOp", new Exception("fail"))));
            _logger.Verify(
                l => l.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Exactly(3));
        }

        [Fact]
        public void AllMethods_IsEnabledThrows_AreSwallowed()
        {
            _logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Throws(new InvalidOperationException("broken"));
            var sut = new MicrosoftLoggingActivityLogger(_logger.Object);

            Assert.Null(Record.Exception(() => sut.LogActivityStart("MyOp")));
            Assert.Null(Record.Exception(() => sut.LogActivityEnd("MyOp", "Ok")));
            Assert.Null(Record.Exception(() => sut.LogException("MyOp", new Exception("fail"))));
        }

        [Fact]
        public void AllMethods_LevelDisabled_DoNotLog()
        {
            _logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(false);
            var sut = new MicrosoftLoggingActivityLogger(_logger.Object);

            sut.LogActivityStart("MyOp");
            sut.LogActivityEnd("MyOp", "Ok");
            sut.LogException("MyOp", new Exception("fail"));

            _logger.Verify(
                l => l.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Never);
        }

        [Fact]
        public void LogException_CustomMessage_IsRenderedInsteadOfExceptionMessage()
        {
            _logger.Setup(l => l.IsEnabled(LogLevel.Error)).Returns(true);
            var sut = new MicrosoftLoggingActivityLogger(_logger.Object);
            var ex = new Exception("fail");

            sut.LogException("MyOp", ex, "custom msg");

            _logger.Verify(
                l => l.Log(LogLevel.Error, It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((state, _) => state!.ToString()!.Contains("custom msg") && !state!.ToString()!.Contains("fail")),
                    ex, It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public void ToActivityLogger_ReturnsInstance()
        {
            var logger = new Mock<ILogger<object>>().Object;
            var activityLogger = logger.ToActivityLogger();
            Assert.NotNull(activityLogger);
            Assert.IsType<MicrosoftLoggingActivityLogger>(activityLogger);
        }

        [Fact]
        public void ToActivityLogger_WithLevel_ReturnsInstance()
        {
            var logger = new Mock<ILogger<object>>().Object;
            var activityLogger = logger.ToActivityLogger(LogLevel.Warning);
            Assert.NotNull(activityLogger);
        }
    }
}
