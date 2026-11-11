using System;
using OpenTelemetry.Exporter;

namespace GMO.OpenTelemetry
{
    /// <summary>
    /// Signal-aware resolution. <c>GetOtlpEndpoint</c> and <c>GetOtlpHeaders</c> report what
    /// configuration says; layering the environment on top here keeps per-signal precedence in one
    /// place and still honours an options type that overrides those accessors.
    /// </summary>
    internal static class OtlpResolutionExtensions
    {
        internal static Uri? ResolveOtlpEndpoint(this IOpenTelemetryOptions options, OtlpSignal signal)
            => OtlpEnvironment.ResolveEndpoint(
                OtlpEnvironment.SanitizeEndpoint(options.GetOtlpEndpoint()), signal, OtlpEnvironment.SystemReader);

        internal static string? ResolveOtlpHeadersFor(this IOpenTelemetryOptions options, OtlpSignal signal)
            => OtlpEnvironment.ResolveHeaders(options.GetOtlpHeaders(), signal, OtlpEnvironment.SystemReader);

        internal static OtlpExportProtocol ResolveOtlpProtocol(this IOpenTelemetryOptions options, OtlpSignal signal)
        {
#if NETFRAMEWORK
            // net48's HttpClient has no HTTP/2 support, so OTLP/gRPC cannot work there at all.
            return OtlpExportProtocol.HttpProtobuf;
#else
            return OtlpEnvironment.ResolveProtocol(options.Otlp?.Protocol, signal, OtlpEnvironment.SystemReader);
#endif
        }
    }
}
