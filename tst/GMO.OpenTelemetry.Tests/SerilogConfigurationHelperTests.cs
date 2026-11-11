using GMO.OpenTelemetry.Serilog;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class SerilogConfigurationHelperTests
    {
        private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        [Fact]
        public void ReadSerilogConfigWithoutSinks_NullLoggerConfig_Throws()
        {
            var config = BuildConfig(new Dictionary<string, string?>());
            Assert.Throws<ArgumentNullException>(() =>
                ((LoggerConfiguration)null!).ReadSerilogConfigWithoutSinks(config));
        }

        [Fact]
        public void ReadSerilogConfigWithoutSinks_NullConfiguration_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new LoggerConfiguration().ReadSerilogConfigWithoutSinks(null!));
        }

        [Fact]
        public void ReadSerilogConfigWithoutSinks_NoSerilogSection_ReturnsUnchanged()
        {
            var config = BuildConfig(new Dictionary<string, string?>());
            var lc = new LoggerConfiguration();
            var result = lc.ReadSerilogConfigWithoutSinks(config);
            Assert.Same(lc, result);
        }

        [Fact]
        public void ReadSerilogConfigWithoutSinks_WithMinimumLevelDefault_ReturnsSameInstance()
        {
            var config = BuildConfig(new Dictionary<string, string?>
            {
                ["Serilog:MinimumLevel:Default"] = "Warning",
            });
            var lc = new LoggerConfiguration();
            var result = lc.ReadSerilogConfigWithoutSinks(config);
            // Fluent API must return the same instance
            Assert.Same(lc, result);
            // Logger must be buildable after config is applied
            using var logger = result.CreateLogger();
            Assert.NotNull(logger);
            // Warning is minimum — debug events should be suppressed
            Assert.False(logger.IsEnabled(LogEventLevel.Debug));
            Assert.True(logger.IsEnabled(LogEventLevel.Warning));
        }

        [Fact]
        public void ReadSerilogConfigWithoutSinks_InvalidMinimumLevel_ReturnsUnchangedInstance()
        {
            var config = BuildConfig(new Dictionary<string, string?>
            {
                ["Serilog:MinimumLevel:Default"] = "NotALevel",
            });
            var lc = new LoggerConfiguration();
            var result = lc.ReadSerilogConfigWithoutSinks(config);
            Assert.Same(lc, result);
            // Logger must still be buildable
            using var logger = result.CreateLogger();
            Assert.NotNull(logger);
        }

        [Fact]
        public void ReadSerilogConfigWithoutSinks_WithOverrides_ReturnsBuildableConfig()
        {
            var config = BuildConfig(new Dictionary<string, string?>
            {
                ["Serilog:MinimumLevel:Default"] = "Information",
                ["Serilog:MinimumLevel:Override:Microsoft"] = "Warning",
                ["Serilog:MinimumLevel:Override:System"] = "Error",
            });
            var lc = new LoggerConfiguration();
            var result = lc.ReadSerilogConfigWithoutSinks(config);
            Assert.Same(lc, result);
            using var logger = result.CreateLogger();
            Assert.NotNull(logger);
            // Default minimum is Information — debug should be suppressed
            Assert.False(logger.IsEnabled(LogEventLevel.Debug));
            Assert.True(logger.IsEnabled(LogEventLevel.Information));
        }

        [Fact]
        public void ReadSerilogConfigWithoutSinks_WithInvalidOverrideLevel_StillBuildable()
        {
            var config = BuildConfig(new Dictionary<string, string?>
            {
                ["Serilog:MinimumLevel:Default"] = "Information",
                ["Serilog:MinimumLevel:Override:Microsoft"] = "NotALevel",
            });
            var lc = new LoggerConfiguration();
            var result = lc.ReadSerilogConfigWithoutSinks(config);
            Assert.Same(lc, result);
            using var logger = result.CreateLogger();
            Assert.NotNull(logger);
        }

        [Fact]
        public void ReadSerilogConfigWithoutSinks_FromLogContext_IsApplied()
        {
            var config = BuildConfig(new Dictionary<string, string?>
            {
                ["Serilog:Enrich:0"] = "FromLogContext",
            });
            var events = new List<LogEvent>();
            var lc = new LoggerConfiguration();
            var result = lc.ReadSerilogConfigWithoutSinks(config);
            Assert.Same(lc, result);
            using var logger = result.WriteTo.Sink(new DelegatingSink(events.Add)).CreateLogger();

            using (global::Serilog.Context.LogContext.PushProperty("Pushed", 42))
                logger.Information("hello");

            var logEvent = Assert.Single(events);
            Assert.Equal("42", logEvent.Properties["Pushed"].ToString());
        }

        private sealed class DelegatingSink : global::Serilog.Core.ILogEventSink
        {
            private readonly Action<LogEvent> _write;

            public DelegatingSink(Action<LogEvent> write) => _write = write;

            public void Emit(LogEvent logEvent) => _write(logEvent);
        }

        [Fact]
        public void ReadSerilogConfigWithoutSinks_WithUnknownEnricher_StillBuildable()
        {
            var config = BuildConfig(new Dictionary<string, string?>
            {
                ["Serilog:Enrich:0"] = "NonExistentEnricher",
            });
            var lc = new LoggerConfiguration();
            var result = lc.ReadSerilogConfigWithoutSinks(config);
            Assert.Same(lc, result);
            using var logger = result.CreateLogger();
            Assert.NotNull(logger);
        }

        [Fact]
        public void ReadSerilogConfigWithoutSinks_WithEmptyEnricherEntry_StillBuildable()
        {
            var config = BuildConfig(new Dictionary<string, string?>
            {
                ["Serilog:Enrich:0"] = "",
            });
            var lc = new LoggerConfiguration();
            var result = lc.ReadSerilogConfigWithoutSinks(config);
            Assert.Same(lc, result);
            using var logger = result.CreateLogger();
            Assert.NotNull(logger);
        }

        [Fact]
        public void ReadSerilogConfigWithoutSinks_AllLevels_ProduceBuildableConfig()
        {
            foreach (LogEventLevel level in Enum.GetValues(typeof(LogEventLevel)))
            {
                var config = BuildConfig(new Dictionary<string, string?>
                {
                    ["Serilog:MinimumLevel:Default"] = level.ToString(),
                });
                var lc = new LoggerConfiguration();
                var result = lc.ReadSerilogConfigWithoutSinks(config);
                Assert.Same(lc, result);
                using var logger = result.CreateLogger();
                Assert.NotNull(logger);
                Assert.True(logger.IsEnabled(level));
            }
        }
    }
}
