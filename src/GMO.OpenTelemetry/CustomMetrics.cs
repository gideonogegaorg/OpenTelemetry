#if !NETFRAMEWORK
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using System.Threading;
using System.Threading.Tasks;
#endif
using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry.Metrics;

namespace GMO.OpenTelemetry
{
#if NETFRAMEWORK
    /// <summary>
    /// On .NET Framework: disposable registration of the <c>process.cpu.percent</c> gauge.
    /// Does not create its own MeterProvider — register
    /// <see cref="Name"/> on your exporting provider (same pattern as modern TFMs).
    /// </summary>
    public sealed class CustomMetrics : IDisposable
#else
    /// <summary>
    /// On .NET Core: implements IHostedService, IDisposable
    /// </summary>
    public sealed class CustomMetrics : IHostedService, IDisposable
#endif
    {
        private static readonly Process _process = Process.GetCurrentProcess();
        private static readonly int _cpuCount = Environment.ProcessorCount;
        private static string _version = typeof(CustomMetrics).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        private TimeSpan _lastTotalCpu;
        private DateTime _lastTimestamp;

        private readonly Meter _meter;
        private bool _disposed;

#if !NETFRAMEWORK
        private const int FlushTimeoutMs = 10000;
        private readonly IServiceProvider? _services;
#endif

        /// <summary>
        /// Name of the Meter (to register in your DI pipeline)
        /// </summary>
        public const string Name = "GMO.OpenTelemetry.metrics";

        internal const string RuntimeMeterName = "System.Runtime";
        internal const string ThreadCountName = "dotnet.thread_pool.thread.count";
        internal const string QueueLengthName = "dotnet.thread_pool.queue.length";

        /// <summary>
        /// A metric view dropping the runtime's thread-pool thread count and queue length. .NET declares
        /// both as monotonic counters although they fall as well as rise, so under delta temporality
        /// every fall reaches the backend as a counter reset and the stored values mean nothing. This
        /// meter publishes them as gauges under the same names instead.
        /// </summary>
        public static MetricStreamConfiguration? DropMisdeclaredRuntimeInstruments(Instrument instrument) =>
            instrument.Meter.Name == RuntimeMeterName
            && (instrument.Name == ThreadCountName || instrument.Name == QueueLengthName)
                ? MetricStreamConfiguration.Drop
                : null;

        public CustomMetrics(/* inject dependencies if needed */)
        {
            try
            {
                _lastTotalCpu = _process.TotalProcessorTime;
            }
            catch
            {
                _lastTotalCpu = TimeSpan.Zero;
            }

            _lastTimestamp = DateTime.UtcNow;

            _meter = new Meter(Name, _version);

            _meter.CreateObservableGauge(
                name: "process.cpu.percent",
                observeValue: GetCpuPercent,
                description: "CPU utilization (%) since last collection",
                unit: "%");

#if NET9_0_OR_GREATER
            // Replaces the runtime's misdeclared counters, which DropMisdeclaredRuntimeInstruments removes.
            _meter.CreateObservableGauge(
                name: ThreadCountName,
                observeValue: () => (long)ThreadPool.ThreadCount,
                unit: "{thread}",
                description: "The number of thread pool threads that currently exist.");
            _meter.CreateObservableGauge(
                name: QueueLengthName,
                observeValue: () => ThreadPool.PendingWorkItemCount,
                unit: "{work_item}",
                description: "The number of work items that are currently queued to be processed by the thread pool.");
#endif
        }

#if !NETFRAMEWORK
        /// <summary>
        /// DI constructor. Capturing the provider lets <see cref="StopAsync"/> flush metrics before the
        /// gauge is unpublished — see the comment there.
        /// </summary>
        public CustomMetrics(IServiceProvider services) : this()
        {
            _services = services;
        }
#endif

        private Measurement<double> GetCpuPercent()
        {
            try
            {
                var now = DateTime.UtcNow;
                var currentTotalCpu = _process.TotalProcessorTime;

                var deltaCpuSec = (currentTotalCpu - _lastTotalCpu).TotalSeconds;
                var deltaWallSec = (now - _lastTimestamp).TotalSeconds;

                _lastTotalCpu = currentTotalCpu;
                _lastTimestamp = now;

                var utilization = deltaWallSec > 0
                    ? (deltaCpuSec / (deltaWallSec * _cpuCount)) * 100
                    : 0;

                return new Measurement<double>(utilization);
            }
            catch
            {
                return new Measurement<double>(0);
            }
        }

#if !NETFRAMEWORK
        // IHostedService no-op; constructor already wires gauge
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            // The MeterProvider's final export runs on its own disposal, which DI performs AFTER this
            // service is torn down (hosted services stop, and singletons dispose, in reverse creation
            // order — and AddOpenTelemetry registers before AddMetrics). Disposing the meter first
            // unpublishes the gauge, so process.cpu.percent never reaches the exporter. Flush here,
            // while it is still live.
            try
            {
                (_services?.GetService(typeof(MeterProvider)) as MeterProvider)?.ForceFlush(FlushTimeoutMs);
            }
            catch
            {
                // Never fail shutdown over a metrics flush.
            }

            Dispose();
            return Task.CompletedTask;
        }
#endif

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _meter.Dispose();
        }
    }
}
