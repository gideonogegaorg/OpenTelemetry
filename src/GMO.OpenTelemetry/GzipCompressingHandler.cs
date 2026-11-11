using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace GMO.OpenTelemetry
{
    public sealed class GzipCompressingHandler : DelegatingHandler
    {
        public GzipCompressingHandler() : base(new HttpClientHandler()) { }

        public GzipCompressingHandler(HttpMessageHandler innerHandler) : base(innerHandler) { }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content != null)
            {
#if NETFRAMEWORK
                var raw = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
#else
                var raw = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
#endif
                var compressedStream = new MemoryStream();
                using (var gzipStream = new GZipStream(compressedStream, CompressionLevel.Fastest, leaveOpen: true))
                {
#if NETFRAMEWORK
                    await gzipStream.WriteAsync(raw, 0, raw.Length, cancellationToken).ConfigureAwait(false);
#else
                    await gzipStream.WriteAsync(raw, cancellationToken).ConfigureAwait(false);
#endif
                }

                ReplaceContent(request, compressedStream);
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

#if NET
        // A synchronous HttpClient.Send reaches this override, never SendAsync; without it the body
        // goes out uncompressed.
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content != null)
            {
                var compressedStream = new MemoryStream();
                using (var gzipStream = new GZipStream(compressedStream, CompressionLevel.Fastest, leaveOpen: true))
                {
                    request.Content.CopyTo(gzipStream, null, cancellationToken);
                }

                ReplaceContent(request, compressedStream);
            }

            return base.Send(request, cancellationToken);
        }
#endif

        private static void ReplaceContent(HttpRequestMessage request, MemoryStream compressedStream)
        {
            compressedStream.Position = 0;

            var originalContentType = request.Content!.Headers.ContentType;
            request.Content = new StreamContent(compressedStream);
            if (originalContentType != null)
                request.Content.Headers.ContentType = originalContentType;
            request.Content.Headers.ContentEncoding.Add("gzip");
        }
    }
}
