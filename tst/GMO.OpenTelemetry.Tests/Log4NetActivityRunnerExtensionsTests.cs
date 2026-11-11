using System;
using System.Diagnostics;
using GMO.OpenTelemetry.Log4Net;
using log4net;
using log4net.Core;
using Moq;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class Log4NetActivityRunnerExtensionsTests : IDisposable
    {
        private readonly ActivitySource _source;
        private readonly ActivityListener _listener;

        public Log4NetActivityRunnerExtensionsTests()
        {
            _source = new ActivitySource("Test.Log4NetActivityRunner");
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

        private static (Mock<ILog> log, Mock<ILogger> logger) BuildLog()
        {
            var logger = new Mock<ILogger>();
            logger.Setup(l => l.IsEnabledFor(It.IsAny<Level>())).Returns(true);

            var log = new Mock<ILog>();
            log.SetupGet(l => l.Logger).Returns(logger.Object);

            return (log, logger);
        }

        [Fact]
        public void ToActivityRunner_FromILog_BindsSourceAndLogger()
        {
            var (log, logger) = BuildLog();

            var runner = log.Object.ToActivityRunner(_source);
            using (runner.Begin("MyOp")) { }

            Assert.Same(_source, runner.ActivitySource);
            logger.Verify(
                l => l.Log(typeof(Log4NetActivityLogger), Level.Info, It.Is<object>(s => s.ToString()!.Contains("MyOp")), null),
                Times.AtLeastOnce);
        }

        [Fact]
        public void ToActivityRunner_FromFuncILog_ResolvesPerCall()
        {
            var (log, _) = BuildLog();
            var calls = 0;
            Func<ILog> factory = () => { calls++; return log.Object; };

            var runner = factory.ToActivityRunner(_source);
            Assert.Equal(0, calls);

            using (runner.Begin("one")) { }
            using (runner.Begin("two")) { }

            Assert.Equal(2, calls);
        }

        [Fact]
        public void ToActivityRunner_RespectsLogLevel()
        {
            var (log, logger) = BuildLog();

            using (log.Object.ToActivityRunner(_source, Level.Debug).Begin("MyOp")) { }

            logger.Verify(
                l => l.Log(typeof(Log4NetActivityLogger), Level.Debug, It.IsAny<object>(), null),
                Times.AtLeastOnce);
        }

        [Fact]
        public void ToActivityRunner_NullLog_Throws() =>
            Assert.Throws<ArgumentNullException>(() => ((ILog)null!).ToActivityRunner(_source));

        [Fact]
        public void ToActivityRunner_NullFactory_Throws() =>
            Assert.Throws<ArgumentNullException>(() => ((Func<ILog>)null!).ToActivityRunner(_source));

        [Fact]
        public void ToActivityRunner_NullActivitySource_Throws()
        {
            var (log, _) = BuildLog();
            Func<ILog> factory = () => log.Object;

            Assert.Throws<ArgumentNullException>(() => log.Object.ToActivityRunner(null!));
            Assert.Throws<ArgumentNullException>(() => factory.ToActivityRunner(null!));
        }
    }
}
