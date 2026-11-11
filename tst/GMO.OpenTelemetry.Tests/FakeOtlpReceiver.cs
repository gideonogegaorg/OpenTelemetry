using System.Net;
using System.Net.Sockets;
using GMO.OpenTelemetry.Tests;
using Xunit;

[assembly: AssemblyFixture(typeof(FakeOtlpReceiver))]

namespace GMO.OpenTelemetry.Tests
{
    /// <summary>
    /// An in-process OTLP/HTTP endpoint that accepts every request with 200, for tests whose exporters
    /// flush on dispose but are not about the network. Pointed at a closed port instead, each flush on
    /// Windows waits about 2 s per resolved address of localhost for the refused connection.
    /// </summary>
    public sealed class FakeOtlpReceiver : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _serving;

        public FakeOtlpReceiver()
        {
            var port = FreePort();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            Endpoint = new Uri($"http://localhost:{port}/");
            _serving = Task.Run(ServeAsync);
        }

        public Uri Endpoint { get; }

        public string LogsEndpoint => new Uri(Endpoint, "v1/logs").AbsoluteUri;

        internal static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task ServeAsync()
        {
            while (true)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                _ = AcceptAsync(context);
            }
        }

        private static async Task AcceptAsync(HttpListenerContext context)
        {
            try
            {
                await context.Request.InputStream.CopyToAsync(Stream.Null).ConfigureAwait(false);
                context.Response.StatusCode = 200;
                context.Response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException)
            {
                // The exporter abandoned the request, or the receiver is shutting down.
            }
        }

        public void Dispose()
        {
            _listener.Close();
            _serving.GetAwaiter().GetResult();
        }
    }
}
