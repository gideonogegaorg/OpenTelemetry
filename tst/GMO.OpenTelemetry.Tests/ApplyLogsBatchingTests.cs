using OpenTelemetry.Logs;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class ApplyLogsBatchingTests
    {
        [Fact]
        public void AppliesTheLogsOptions()
        {
            var processor = new LogRecordExportProcessorOptions();

            processor.ApplyLogsBatching(new TestOptions { LogsMaxBatchSize = 32, LogsMaxQueueSize = 500, LogsExportDelayMs = 700, LogsExportTimeoutMs = 9000 });

            var batch = processor.BatchExportProcessorOptions;
            Assert.Equal(32, batch.MaxExportBatchSize);
            Assert.Equal(500, batch.MaxQueueSize);
            Assert.Equal(700, batch.ScheduledDelayMilliseconds);
            Assert.Equal(9000, batch.ExporterTimeoutMilliseconds);
        }

        // The SDK default is 512 records, enough to exceed New Relic's 1 MB request limit.
        [Fact]
        public void DefaultsToTheLibrarysBatchSize()
        {
            var processor = new LogRecordExportProcessorOptions();

            processor.ApplyLogsBatching(new TestOptions());

            Assert.Equal(64, processor.BatchExportProcessorOptions.MaxExportBatchSize);
        }

        [Fact]
        public void NonPositiveBatchSize_UsesTheDefault()
        {
            var processor = new LogRecordExportProcessorOptions();

            processor.ApplyLogsBatching(new TestOptions { LogsMaxBatchSize = 0 });

            Assert.Equal(64, processor.BatchExportProcessorOptions.MaxExportBatchSize);
        }

        [Fact]
        public void NonPositiveQueueDelayAndTimeout_KeepTheSdkDefaults()
        {
            var defaults = new LogRecordExportProcessorOptions().BatchExportProcessorOptions;
            var processor = new LogRecordExportProcessorOptions();

            processor.ApplyLogsBatching(new TestOptions { LogsMaxQueueSize = 0, LogsExportDelayMs = -1, LogsExportTimeoutMs = 0 });

            var batch = processor.BatchExportProcessorOptions;
            Assert.Equal(defaults.MaxQueueSize, batch.MaxQueueSize);
            Assert.Equal(defaults.ScheduledDelayMilliseconds, batch.ScheduledDelayMilliseconds);
            Assert.Equal(defaults.ExporterTimeoutMilliseconds, batch.ExporterTimeoutMilliseconds);
        }

        [Fact]
        public void NullArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => ((LogRecordExportProcessorOptions)null!).ApplyLogsBatching(new TestOptions()));
            Assert.Throws<ArgumentNullException>(() => new LogRecordExportProcessorOptions().ApplyLogsBatching(null!));
        }
    }
}
