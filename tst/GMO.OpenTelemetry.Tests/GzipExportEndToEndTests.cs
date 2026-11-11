using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using GMO.OpenTelemetry.Serilog;
using Microsoft.Extensions.Configuration;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    [CollectionDefinition(nameof(OtlpCompressionEnvironment), DisableParallelization = true)]
    public class OtlpCompressionEnvironment { }

    /// <summary>
    /// Exports a real span, and a real log through the Serilog sink, to a local listener and counts the
    /// gzip layers on the wire. The trace exporter reads OTEL_EXPORTER_OTLP_COMPRESSION itself, so
    /// compressing a second way on top of it sends gzip inside gzip, which a collector cannot decode.
    /// </summary>
    [Collection(nameof(OtlpCompressionEnvironment))]
    public class GzipExportEndToEndTests
    {
        private const string CompressionVariable = "OTEL_EXPORTER_OTLP_COMPRESSION";

        [Theory]
        [InlineData(null)]
        [InlineData("gzip")]
        [InlineData("none")]
        public async Task HttpProtobuf_BodyIsGzippedExactlyOnce(string? environmentCompression)
        {
            var body = await Capture(environmentCompression, port => ExportOneSpan(port, enableGzip: true));

            Assert.Equal(1, GzipLayers(body));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("gzip")]
        public async Task HttpProtobuf_GzipDisabled_IsNotCompressed(string? environmentCompression)
        {
            var body = await Capture(environmentCompression, port => ExportOneSpan(port, enableGzip: false));

            Assert.Equal(0, GzipLayers(body));
        }

        // The sink has no compression setting and ignores the variable, so only the library compresses its logs.
        [Theory]
        [InlineData(null)]
        [InlineData("gzip")]
        public async Task SerilogSink_HttpProtobuf_LogIsGzippedExactlyOnce(string? environmentCompression)
        {
            var body = await Capture(environmentCompression, port => ExportOneLog(port, enableGzip: true));

            Assert.Equal(1, GzipLayers(body));
        }

        [Fact]
        public async Task SerilogSink_HttpProtobuf_GzipDisabled_IsNotCompressed()
        {
            var body = await Capture("gzip", port => ExportOneLog(port, enableGzip: false));

            Assert.Equal(0, GzipLayers(body));
        }

        private static void ExportOneSpan(int port, bool enableGzip)
        {
            var sourceName = "gzip-e2e-" + Guid.NewGuid().ToString("N");
            using var source = new ActivitySource(sourceName);
            using (Sdk.CreateTracerProviderBuilder()
                .AddSource(sourceName)
                .AddOtlpExporter(opt =>
                {
                    opt.Protocol = OtlpExportProtocol.HttpProtobuf;
                    opt.Endpoint = new Uri($"http://localhost:{port}/v1/traces");
                    opt.ExportProcessorType = ExportProcessorType.Simple;
                    opt.ApplyGzipCompression(new TestOptions { EnableGzipCompression = enableGzip });
                })
                .Build())
            {
                using (source.StartActivity("span")) { }
            }
        }

        private static void ExportOneLog(int port, bool enableGzip)
        {
            var options = new TestOptions
            {
                Enabled = true,
                EnableLogging = true,
                EnableGzipCompression = enableGzip,
                LoggingEndpoint = $"http://localhost:{port}/v1/logs",
                HeadersOverride = "api-key=test",
                LogsExportDelayMs = 50,
            };

            // Disposing flushes the batch.
            using var sink = new ChunkingOpenTelemetrySink(options, new ConfigurationBuilder().Build());
            sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null,
                new MessageTemplateParser().Parse("gzip e2e log"), Array.Empty<LogEventProperty>()));
        }

        private static async Task<byte[]> Capture(string? environmentCompression, Action<int> send)
        {
            var previous = Environment.GetEnvironmentVariable(CompressionVariable);
            Environment.SetEnvironmentVariable(CompressionVariable, environmentCompression);
            try
            {
                var port = FreePort();
                using var listener = new HttpListener();
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Start();
                var received = ReceiveOne(listener);

                send(port);

                var completed = await Task.WhenAny(received, Task.Delay(TimeSpan.FromSeconds(10)));
                Assert.Same(received, completed);

                return await received;
            }
            finally
            {
                Environment.SetEnvironmentVariable(CompressionVariable, previous);
            }
        }

        // Answers as soon as the body is read: a simple processor exports synchronously, so a response
        // withheld until the provider is disposed stalls the export for its full timeout.
        private static async Task<byte[]> ReceiveOne(HttpListener listener)
        {
            var context = await listener.GetContextAsync();
            using var buffer = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(buffer);
            context.Response.StatusCode = 200;
            context.Response.Close();
            return buffer.ToArray();
        }

        private static int GzipLayers(byte[] body)
        {
            var layers = 0;
            while (body.Length > 2 && body[0] == 0x1f && body[1] == 0x8b)
            {
                using var input = new MemoryStream(body);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                gzip.CopyTo(output);
                body = output.ToArray();
                layers++;
            }

            return layers;
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }
}
