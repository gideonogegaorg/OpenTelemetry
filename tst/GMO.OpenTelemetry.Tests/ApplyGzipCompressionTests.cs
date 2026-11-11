using OpenTelemetry.Exporter;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class ApplyGzipCompressionTests
    {
        [Theory]
        [InlineData(OtlpExportProtocol.HttpProtobuf, true, OtlpExportCompression.GZip)]
        [InlineData(OtlpExportProtocol.HttpProtobuf, false, OtlpExportCompression.None)]
#pragma warning disable CS0618 // Grpc is obsolete on netfx; the setting must still hold for it.
        [InlineData(OtlpExportProtocol.Grpc, true, OtlpExportCompression.GZip)]
        [InlineData(OtlpExportProtocol.Grpc, false, OtlpExportCompression.None)]
#pragma warning restore CS0618
        public void SetsTheExporterCompression(OtlpExportProtocol protocol, bool enabled, OtlpExportCompression expected)
        {
            var opt = new OtlpExporterOptions { Protocol = protocol };

            opt.ApplyGzipCompression(new TestOptions { EnableGzipCompression = enabled });

            Assert.Equal(expected, opt.Compression);
        }

        [Fact]
        public void IsOnByDefault()
        {
            var opt = new OtlpExporterOptions();

            opt.ApplyGzipCompression(new TestOptions());

            Assert.Equal(OtlpExportCompression.GZip, opt.Compression);
        }

        [Fact]
        public void LeavesHeadersAndHttpClientFactoryAlone()
        {
            var opt = new OtlpExporterOptions { Protocol = OtlpExportProtocol.HttpProtobuf, Headers = "api-key=test123" };
            var factory = opt.HttpClientFactory;

            var returned = opt.ApplyGzipCompression(new TestOptions());

            Assert.Same(opt, returned);
            Assert.Equal("api-key=test123", opt.Headers);
            Assert.Same(factory, opt.HttpClientFactory);
        }

        [Fact]
        public void NullArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => ((OtlpExporterOptions)null!).ApplyGzipCompression(new TestOptions()));
            Assert.Throws<ArgumentNullException>(() => new OtlpExporterOptions().ApplyGzipCompression(null!));
        }
    }
}
