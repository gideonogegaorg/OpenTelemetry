using GMO.OpenTelemetry.Log4Net;
using log4net;
using log4net.Core;
using Moq;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class Log4NetActivityLoggerTests
    {
        private static (Mock<ILog> log, Mock<ILogger> loggerMock) BuildLog(bool enabled = true)
        {
            var loggerMock = new Mock<ILogger>();
            loggerMock.Setup(l => l.IsEnabledFor(It.IsAny<Level>())).Returns(enabled);
            var log = new Mock<ILog>();
            log.Setup(l => l.Logger).Returns(loggerMock.Object);
            return (log, loggerMock);
        }

        [Fact]
        public void Constructor_NullLog_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(() =>
                new Log4NetActivityLogger(null!));
        }

        [Fact]
        public void LogActivityStart_LogsWhenEnabled()
        {
            var (log, loggerMock) = BuildLog(true);
            var sut = new Log4NetActivityLogger(log.Object);
            sut.LogActivityStart("MyOp");
            loggerMock.Verify(l =>
                l.Log(typeof(Log4NetActivityLogger), Level.Info,
                    It.Is<string>(s => s.Contains("MyOp")), null), Times.Once);
        }

        [Fact]
        public void LogActivityStart_SkipsWhenDisabled()
        {
            var (log, loggerMock) = BuildLog(false);
            var sut = new Log4NetActivityLogger(log.Object);
            sut.LogActivityStart("MyOp");
            loggerMock.Verify(l =>
                l.Log(It.IsAny<System.Type>(), It.IsAny<Level>(),
                    It.IsAny<object>(), It.IsAny<System.Exception>()), Times.Never);
        }

        [Fact]
        public void LogActivityEnd_LogsWhenEnabled()
        {
            var (log, loggerMock) = BuildLog(true);
            var sut = new Log4NetActivityLogger(log.Object);
            sut.LogActivityEnd("MyOp", "Ok");
            loggerMock.Verify(l =>
                l.Log(typeof(Log4NetActivityLogger), Level.Info,
                    It.Is<string>(s => s.Contains("MyOp") && s.Contains("Ok")), null), Times.Once);
        }

        [Fact]
        public void LogException_LogsAtError()
        {
            var (log, loggerMock) = BuildLog(true);
            var sut = new Log4NetActivityLogger(log.Object);
            var ex = new System.Exception("fail");
            sut.LogException("MyOp", ex);
            loggerMock.Verify(l =>
                l.Log(typeof(Log4NetActivityLogger), Level.Error,
                    It.IsAny<object>(), ex), Times.Once);
        }

        [Fact]
        public void LogException_CustomMessage_UsesMessage()
        {
            var (log, loggerMock) = BuildLog(true);
            var sut = new Log4NetActivityLogger(log.Object);
            var ex = new System.Exception("fail");
            sut.LogException("MyOp", ex, "custom msg");
            loggerMock.Verify(l =>
                l.Log(typeof(Log4NetActivityLogger), Level.Error, "custom msg", ex), Times.Once);
        }

        [Fact]
        public void AllMethods_LoggerThrows_AreSwallowed()
        {
            var (log, loggerMock) = BuildLog(true);
            loggerMock.Setup(l => l.Log(It.IsAny<System.Type>(), It.IsAny<Level>(), It.IsAny<object>(), It.IsAny<System.Exception>()))
                .Throws(new System.InvalidOperationException("appender down"));
            var sut = new Log4NetActivityLogger(log.Object);

            Assert.Null(Record.Exception(() => sut.LogActivityStart("MyOp")));
            Assert.Null(Record.Exception(() => sut.LogActivityEnd("MyOp", "Ok")));
            Assert.Null(Record.Exception(() => sut.LogException("MyOp", new System.Exception("fail"))));
            loggerMock.Verify(l => l.Log(It.IsAny<System.Type>(), It.IsAny<Level>(), It.IsAny<object>(), It.IsAny<System.Exception>()),
                Times.Exactly(3));
        }

        [Fact]
        public void AllMethods_LoggerPropertyThrows_AreSwallowed()
        {
            var log = new Mock<ILog>();
            log.Setup(l => l.Logger).Throws(new System.InvalidOperationException("repository shut down"));
            var sut = new Log4NetActivityLogger(log.Object);

            Assert.Null(Record.Exception(() => sut.LogActivityStart("MyOp")));
            Assert.Null(Record.Exception(() => sut.LogActivityEnd("MyOp", "Ok")));
            Assert.Null(Record.Exception(() => sut.LogException("MyOp", new System.Exception("fail"))));
        }

        [Fact]
        public void LogActivityEndAndLogException_SkipWhenDisabled()
        {
            var (log, loggerMock) = BuildLog(false);
            var sut = new Log4NetActivityLogger(log.Object);
            sut.LogActivityEnd("MyOp", "Ok");
            sut.LogException("MyOp", new System.Exception("fail"));
            loggerMock.Verify(l =>
                l.Log(It.IsAny<System.Type>(), It.IsAny<Level>(),
                    It.IsAny<object>(), It.IsAny<System.Exception>()), Times.Never);
        }

        [Fact]
        public void LogActivityStart_UsesConfiguredLevel()
        {
            var (log, loggerMock) = BuildLog(true);
            var sut = new Log4NetActivityLogger(log.Object, Level.Debug);
            sut.LogActivityStart("MyOp");
            loggerMock.Verify(l => l.IsEnabledFor(Level.Debug), Times.Once);
            loggerMock.Verify(l =>
                l.Log(typeof(Log4NetActivityLogger), Level.Debug,
                    It.Is<string>(s => s.Contains("MyOp")), null), Times.Once);
        }

        [Fact]
        public void ToActivityLogger_ReturnsInstance()
        {
            var (log, _) = BuildLog();
            var activityLogger = log.Object.ToActivityLogger();
            Assert.NotNull(activityLogger);
            Assert.IsType<Log4NetActivityLogger>(activityLogger);
        }

        [Fact]
        public void ToActivityLogger_WithLevel_ReturnsInstance()
        {
            var (log, _) = BuildLog();
            var activityLogger = log.Object.ToActivityLogger(Level.Debug);
            Assert.NotNull(activityLogger);
        }
    }
}
