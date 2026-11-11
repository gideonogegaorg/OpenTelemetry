using System.Threading;
using System.Threading.Tasks;
#if !NETFRAMEWORK
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
#endif
using OpenTelemetry.Metrics;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class CustomMetricsTests
    {
        [Fact]
        public void Name_IsExpectedConstant()
        {
            Assert.Equal("GMO.OpenTelemetry.metrics", CustomMetrics.Name);
        }

        [Fact]
        public void DropMisdeclaredRuntimeInstruments_DropsOnlyTheRuntimeThreadPoolCounters()
        {
            using var runtime = new System.Diagnostics.Metrics.Meter(CustomMetrics.RuntimeMeterName + ".Test");
            using var actualRuntime = new System.Diagnostics.Metrics.Meter(CustomMetrics.RuntimeMeterName);
            using var other = new System.Diagnostics.Metrics.Meter("Other");

            Assert.Same(MetricStreamConfiguration.Drop, CustomMetrics.DropMisdeclaredRuntimeInstruments(actualRuntime.CreateObservableCounter(CustomMetrics.ThreadCountName, () => 1L)));
            Assert.Same(MetricStreamConfiguration.Drop, CustomMetrics.DropMisdeclaredRuntimeInstruments(actualRuntime.CreateObservableCounter(CustomMetrics.QueueLengthName, () => 1L)));
            Assert.Null(CustomMetrics.DropMisdeclaredRuntimeInstruments(actualRuntime.CreateObservableCounter("dotnet.thread_pool.work_item.count", () => 1L)));
            Assert.Null(CustomMetrics.DropMisdeclaredRuntimeInstruments(other.CreateObservableGauge(CustomMetrics.ThreadCountName, () => 1L)));
            Assert.Null(CustomMetrics.DropMisdeclaredRuntimeInstruments(runtime.CreateObservableCounter(CustomMetrics.ThreadCountName, () => 1L)));
        }

        [Fact]
        public void Constructor_CreatesWithoutThrowing()
        {
            var exception = Record.Exception(() =>
            {
                using var metrics = new CustomMetrics();
                Assert.NotNull(metrics);
            });
            Assert.Null(exception);
        }

        [Fact]
        public void Dispose_CanBeCalledMultipleTimes()
        {
            var metrics = new CustomMetrics();
            metrics.Dispose();
            var exception = Record.Exception(() => metrics.Dispose());
            Assert.Null(exception);
        }

        [Fact]
        public void Constructor_CpuGaugeCallback_DoesNotThrow()
        {
            // Verify the CPU gauge can be created and disposed without errors
            // This exercises the try/catch around _process.TotalProcessorTime
            using var metrics = new CustomMetrics();
            // Force a second construction to exercise the delta path
            var exception = Record.Exception(() =>
            {
                using var metrics2 = new CustomMetrics();
            });
            Assert.Null(exception);
        }

#if !NETFRAMEWORK
        [Fact]
        public async Task StartAsync_ReturnsCompletedTask()
        {
            using var metrics = new CustomMetrics();
            var task = metrics.StartAsync(CancellationToken.None);
            await task;
            Assert.True(task.IsCompletedSuccessfully);
        }

        [Fact]
        public async Task StopAsync_DisposesAndReturnsCompletedTask()
        {
            var metrics = new CustomMetrics();
            var task = metrics.StopAsync(CancellationToken.None);
            await task;
            Assert.True(task.IsCompletedSuccessfully);
            var exception = Record.Exception(() => metrics.Dispose());
            Assert.Null(exception);
        }

        private sealed class CapturingExporter : BaseExporter<Metric>
        {
            public List<string> Names { get; } = [];

            public override ExportResult Export(in Batch<Metric> batch)
            {
                foreach (var metric in batch)
                {
                    Names.Add(metric.Name);
                }

                return ExportResult.Success;
            }
        }

        // Regression: StopAsync used to dispose the meter outright. Hosted services stop, and DI
        // disposes singletons, in reverse creation order — and AddOpenTelemetry registers before
        // AddMetrics — so the gauge was unpublished before MeterProvider.Dispose ran its final
        // export, and process.cpu.percent never reached the exporter on net6/8/9.
        [Fact]
        public async Task StopAsync_ExportsCpuGauge_BeforeDisposingMeter()
        {
            var exporter = new CapturingExporter();
            using var provider = Sdk.CreateMeterProviderBuilder()
                .AddMeter(CustomMetrics.Name)
                .AddReader(new BaseExportingMetricReader(exporter))
                .Build();

            var services = new ServiceCollection();
            services.AddSingleton(provider);
            using var serviceProvider = services.BuildServiceProvider();

            var metrics = new CustomMetrics(serviceProvider);
            await metrics.StopAsync(CancellationToken.None);

            Assert.Contains("process.cpu.percent", exporter.Names);
        }

#if NET9_0_OR_GREATER
        private sealed class TypeCapturingExporter : BaseExporter<Metric>
        {
            public List<(string Meter, string Name, MetricType Type)> Metrics { get; } = [];

            public override ExportResult Export(in Batch<Metric> batch)
            {
                foreach (var metric in batch)
                    Metrics.Add((metric.MeterName, metric.Name, metric.MetricType));
                return ExportResult.Success;
            }
        }

        // .NET declares these as monotonic counters; under delta temporality every fall reached New Relic
        // as a counter reset. They must arrive once, as gauges.
        [Fact]
        public void ThreadPoolThreadCountAndQueueLength_ExportAsGaugesOnly()
        {
            var exporter = new TypeCapturingExporter();
            using var metrics = new CustomMetrics();
            using (var provider = Sdk.CreateMeterProviderBuilder()
                .AddMeter(CustomMetrics.RuntimeMeterName, CustomMetrics.Name)
                .AddView(CustomMetrics.DropMisdeclaredRuntimeInstruments)
                .AddReader(new BaseExportingMetricReader(exporter) { TemporalityPreference = MetricReaderTemporalityPreference.Delta })
                .Build())
            {
                provider.ForceFlush();
            }

            foreach (var name in new[] { CustomMetrics.ThreadCountName, CustomMetrics.QueueLengthName })
            {
                var exported = exporter.Metrics.Where(m => m.Name == name).Distinct().ToList();
                var only = Assert.Single(exported);
                Assert.Equal(CustomMetrics.Name, only.Meter);
                Assert.Equal(MetricType.LongGauge, only.Type);
            }
        }
#endif

        private sealed class ThrowingServiceProvider : IServiceProvider
        {
            public object GetService(Type serviceType) => throw new InvalidOperationException("resolution failed");
        }

        // A metrics flush must never be able to break host shutdown.
        [Fact]
        public async Task StopAsync_WhenProviderLookupThrows_DoesNotPropagate()
        {
            var metrics = new CustomMetrics(new ThrowingServiceProvider());

            var exception = await Record.ExceptionAsync(() => metrics.StopAsync(CancellationToken.None));

            Assert.Null(exception);
        }

        [Fact]
        public async Task StopAsync_WithoutMeterProviderRegistered_DoesNotThrow()
        {
            var services = new ServiceCollection();
            using var serviceProvider = services.BuildServiceProvider();

            var metrics = new CustomMetrics(serviceProvider);
            var exception = await Record.ExceptionAsync(() => metrics.StopAsync(CancellationToken.None));

            Assert.Null(exception);
        }
#endif
    }
}
