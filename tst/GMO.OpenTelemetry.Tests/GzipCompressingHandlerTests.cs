using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class GzipCompressingHandlerTests
    {
        [Fact]
        public async Task SendAsync_CompressesRequestBody()
        {
            var original = "Hello, this is a test payload for gzip compression.";
            var capture = new CapturingHandler();
            var handler = new GzipCompressingHandler(capture);
            var client = new HttpClient(handler);

            var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/test")
            {
                Content = new StringContent(original, Encoding.UTF8, "application/json")
            };

            await client.SendAsync(request, TestContext.Current.CancellationToken);

            Assert.NotNull(capture.CapturedContent);
            Assert.Contains("gzip", capture.CapturedContent!.Headers.ContentEncoding);

#if NETFRAMEWORK
            var compressedBytes = await capture.CapturedContent.ReadAsByteArrayAsync();
#else
            var compressedBytes = await capture.CapturedContent.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
#endif
            var decompressed = DecompressGzip(compressedBytes);
            Assert.Equal(original, decompressed);
        }

        [Fact]
        public async Task SendAsync_PreservesContentType()
        {
            var capture = new CapturingHandler();
            var handler = new GzipCompressingHandler(capture);
            var client = new HttpClient(handler);

            var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/test")
            {
                Content = new StringContent("data", Encoding.UTF8, "application/json")
            };

            await client.SendAsync(request, TestContext.Current.CancellationToken);

            Assert.NotNull(capture.CapturedContent);
            Assert.Equal("application/json", capture.CapturedContent!.Headers.ContentType?.MediaType);
        }

        [Fact]
        public async Task SendAsync_NullContent_PassesThrough()
        {
            var capture = new CapturingHandler();
            var handler = new GzipCompressingHandler(capture);
            var client = new HttpClient(handler);

            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");

            var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Null(capture.CapturedContent);
        }

        [Fact]
        public async Task SendAsync_CancelledToken_ThrowsBeforeReachingInnerHandler()
        {
            var inner = new TokenIgnoringHandler();
            using var invoker = new HttpMessageInvoker(new GzipCompressingHandler(inner));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/test")
            {
                Content = new StringContent("payload", Encoding.UTF8, "application/json")
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invoker.SendAsync(request, cts.Token));
            Assert.False(inner.Called);
        }

        private sealed class TokenIgnoringHandler : HttpMessageHandler
        {
            public bool Called { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Called = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        }

#if NET
        // The OTLP exporter sends synchronously on .NET 5+, which reaches Send and never SendAsync.
        [Fact]
        public void Send_Synchronous_CompressesRequestBody()
        {
            const string original = "Synchronous payload for gzip compression.";
            var capture = new SyncCapturingHandler();
            using var client = new HttpClient(new GzipCompressingHandler(capture));

            client.Send(new HttpRequestMessage(HttpMethod.Post, "http://localhost/test")
            {
                Content = new StringContent(original, Encoding.UTF8, "application/x-protobuf")
            }, TestContext.Current.CancellationToken);

            Assert.Contains("gzip", capture.ContentEncoding);
            Assert.Equal("application/x-protobuf", capture.MediaType);
            Assert.Equal(original, DecompressGzip(capture.Body!));
        }

        [Fact]
        public void Send_Synchronous_NullContent_PassesThrough()
        {
            var capture = new SyncCapturingHandler();
            using var client = new HttpClient(new GzipCompressingHandler(capture));

            var response = client.Send(new HttpRequestMessage(HttpMethod.Get, "http://localhost/test"),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(capture.Called);
            Assert.Null(capture.Body);
        }

        [Fact]
        public void Send_Synchronous_NoContentType_CompressesWithoutAddingOne()
        {
            var capture = new SyncCapturingHandler();
            using var client = new HttpClient(new GzipCompressingHandler(capture));

            client.Send(new HttpRequestMessage(HttpMethod.Post, "http://localhost/test")
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("untyped payload"))
            }, TestContext.Current.CancellationToken);

            Assert.Contains("gzip", capture.ContentEncoding);
            Assert.Null(capture.MediaType);
            Assert.Equal("untyped payload", DecompressGzip(capture.Body!));
        }

        private sealed class SyncCapturingHandler : HttpMessageHandler
        {
            public bool Called { get; private set; }
            public byte[]? Body { get; private set; }
            public string? MediaType { get; private set; }
            public string[] ContentEncoding { get; private set; } = [];

            protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Called = true;
                if (request.Content == null)
                    return new HttpResponseMessage(HttpStatusCode.OK);

                using var buffer = new MemoryStream();
                request.Content!.CopyTo(buffer, null, cancellationToken);
                Body = buffer.ToArray();
                MediaType = request.Content.Headers.ContentType?.MediaType;
                ContentEncoding = [.. request.Content.Headers.ContentEncoding];
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(Send(request, cancellationToken));
        }
#endif

        private static string DecompressGzip(byte[] compressed)
        {
            using var input = new MemoryStream(compressed);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private sealed class CapturingHandler : HttpMessageHandler
        {
            public HttpContent? CapturedContent { get; private set; }
            private byte[]? _capturedBytes;
            private MediaTypeHeaderValue? _capturedContentType;
            private string? _capturedEncoding;

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Content != null)
                {
#if NETFRAMEWORK
                    _capturedBytes = await request.Content.ReadAsByteArrayAsync();
#else
                    _capturedBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
#endif
                    _capturedContentType = request.Content.Headers.ContentType;
                    _capturedEncoding = string.Join(",", request.Content.Headers.ContentEncoding);

                    var clone = new ByteArrayContent(_capturedBytes);
                    if (_capturedContentType != null)
                        clone.Headers.ContentType = _capturedContentType;
                    if (!string.IsNullOrEmpty(_capturedEncoding))
                        clone.Headers.ContentEncoding.Add(_capturedEncoding);
                    CapturedContent = clone;
                }

                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        }
    }
}
