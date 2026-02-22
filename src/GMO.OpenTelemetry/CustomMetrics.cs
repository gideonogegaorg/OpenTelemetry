using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace GMO.OpenTelemetry
{
    /// <summary>
    /// Implements IHostedService, IDisposable for OpenTelemetry process CPU metrics.
    /// </summary>
    public sealed class CustomMetrics : IHostedService, IDisposable
    {
        private static readonly Process _process = Process.GetCurrentProcess();
        private static readonly int _cpuCount = Environment.ProcessorCount;
        private static string _version = typeof(CustomMetrics).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        private TimeSpan _lastTotalCpu;
        private DateTime _lastTimestamp;

        private readonly Meter _meter;
        private readonly MeterProvider _provider;

        /// <summary>
        /// Name of the Meter (to register in your DI pipeline)
        /// </summary>
        public const string Name = "GMO.OpenTelemetry.metrics";

        public CustomMetrics(/* inject dependencies if needed */)
        {
            _lastTotalCpu = _process.TotalProcessorTime;
            _lastTimestamp = DateTime.UtcNow;

            _meter = new Meter(Name, _version);

            _meter.CreateObservableGauge(
                name: "process.cpu.percent",
                observeValue: GetCpuPercent,
                description: "CPU utilization (%) since last collection",
                unit: "%");

            // Ensure this meter is picked up by the OTLP pipeline
            _provider = Sdk.CreateMeterProviderBuilder()
                .AddMeter(Name)
                .Build();
        }

        private Measurement<double> GetCpuPercent()
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

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Dispose();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _provider?.Dispose();
            _meter.Dispose();
        }
    }
}
