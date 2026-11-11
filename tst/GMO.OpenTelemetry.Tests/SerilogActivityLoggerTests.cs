using GMO.OpenTelemetry.Serilog;
using Moq;
using Serilog;
using Serilog.Events;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class SerilogActivityLoggerTests
    {
        private readonly Mock<ILogger> _logger = new();

        [Fact]
        public void Constructor_NullLogger_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(() =>
                new SerilogActivityLogger(null!));
        }

        [Fact]
        public void LogActivityStart_WritesAtConfiguredLevel()
        {
            var sut = new SerilogActivityLogger(_logger.Object);
            sut.LogActivityStart("MyOp");
            // Write<string>(level, template, propertyValue)
            _logger.Verify(l => l.Write(LogEventLevel.Information,
                It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public void LogActivityEnd_WritesAtConfiguredLevel()
        {
            var sut = new SerilogActivityLogger(_logger.Object);
            sut.LogActivityEnd("MyOp", "Ok");
            _logger.Verify(l => l.Write(LogEventLevel.Information,
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public void LogException_WritesAtErrorLevel()
        {
            var sut = new SerilogActivityLogger(_logger.Object);
            var ex = new System.Exception("fail");
            sut.LogException("MyOp", ex);
            _logger.Verify(l => l.Write(LogEventLevel.Error,
                ex, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public void LogActivityStart_UsesDebugLevel_WhenConfigured()
        {
            var sut = new SerilogActivityLogger(_logger.Object, LogEventLevel.Debug);
            sut.LogActivityStart("MyOp");
            _logger.Verify(l => l.Write(LogEventLevel.Debug,
                It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public void AllMethods_LoggerThrows_AreSwallowed()
        {
            _logger.Setup(l => l.Write(It.IsAny<LogEventLevel>(), It.IsAny<string>(), It.IsAny<string>()))
                .Throws(new System.InvalidOperationException("sink down"));
            _logger.Setup(l => l.Write(It.IsAny<LogEventLevel>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .Throws(new System.InvalidOperationException("sink down"));
            _logger.Setup(l => l.Write(It.IsAny<LogEventLevel>(), It.IsAny<System.Exception>(), It.IsAny<string>(), It.IsAny<string>()))
                .Throws(new System.InvalidOperationException("sink down"));
            var sut = new SerilogActivityLogger(_logger.Object);

            Assert.Null(Record.Exception(() => sut.LogActivityStart("MyOp")));
            Assert.Null(Record.Exception(() => sut.LogActivityEnd("MyOp", "Ok")));
            Assert.Null(Record.Exception(() => sut.LogException("MyOp", new System.Exception("fail"))));
            _logger.Verify(l => l.Write(LogEventLevel.Information, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            _logger.Verify(l => l.Write(LogEventLevel.Information, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            _logger.Verify(l => l.Write(LogEventLevel.Error, It.IsAny<System.Exception>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public void LogException_CustomMessage_IsUsedAsTemplate()
        {
            var sut = new SerilogActivityLogger(_logger.Object);
            var ex = new System.Exception("fail");
            sut.LogException("MyOp", ex, "custom msg");
            _logger.Verify(l => l.Write(LogEventLevel.Error, ex, "custom msg", "MyOp"), Times.Once);
        }

        [Fact]
        public void ToActivityLogger_ReturnsInstance()
        {
            var activityLogger = _logger.Object.ToActivityLogger();
            Assert.NotNull(activityLogger);
            Assert.IsType<SerilogActivityLogger>(activityLogger);
        }
    }
}
