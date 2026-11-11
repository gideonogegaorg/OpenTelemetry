using GMO.OpenTelemetry.Log4Net;
using log4net.Appender;
using log4net.Core;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    [Collection(nameof(TelemetryDiagnosticsState))]
    public class Log4NetTelemetryDiagnosticsTests : IDisposable
    {
        private readonly MemoryAppender _memory = new MemoryAppender();
        private readonly log4net.ILog _log;

        public Log4NetTelemetryDiagnosticsTests()
        {
            TelemetryDiagnostics.ResetForTests();
            var repository = log4net.LogManager.CreateRepository("diag-" + Guid.NewGuid().ToString("N"));
            log4net.Config.BasicConfigurator.Configure(repository, _memory);
            _log = log4net.LogManager.GetLogger(repository.Name, "telemetry");
        }

        public void Dispose()
        {
            TelemetryDiagnostics.ResetForTests();
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void StartTelemetryDiagnostics_RoutesReportsToTheLog()
        {
            _log.StartTelemetryDiagnostics();

            TelemetryDiagnostics.Report("OpenTelemetry-Sdk", "ExportFailed", "k1", isError: true, "timed out");
            TelemetryDiagnostics.Report("OpenTelemetry-Sdk", "QueueFull", "k2", isError: false, "dropped");

            var events = _memory.GetEvents();
            Assert.Equal(2, events.Length);
            Assert.Equal(Level.Error, events[0].Level);
            Assert.Equal(Level.Warn, events[1].Level);
            Assert.All(events, e => Assert.True(e.LookupProperty(TelemetryDiagnostics.MarkerProperty) as bool?));
            Assert.Equal("ExportFailed", events[0].LookupProperty("TelemetryEvent"));
            Assert.Equal(0, events[0].LookupProperty("TelemetrySuppressed"));
            Assert.Contains("timed out", events[0].RenderedMessage);
        }

        [Fact]
        public void StartTelemetryDiagnostics_LastLoggerWins()
        {
            var first = new MemoryAppender();
            var repository = log4net.LogManager.CreateRepository("diag-" + Guid.NewGuid().ToString("N"));
            log4net.Config.BasicConfigurator.Configure(repository, first);
            log4net.LogManager.GetLogger(repository.Name, "first").StartTelemetryDiagnostics();

            _log.StartTelemetryDiagnostics();
            TelemetryDiagnostics.Report("S", "E", "k", isError: true, "detail");

            Assert.Empty(first.GetEvents());
            Assert.Single(_memory.GetEvents());
        }
    }
}
