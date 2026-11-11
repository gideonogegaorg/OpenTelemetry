#if !NETFRAMEWORK
using System;
using OpenTelemetry.Exporter;

namespace GMO.OpenTelemetry
{
    /// <summary>
    /// Configuration-bindable view of the OTLP exporter settings. Bound instead of
    /// <see cref="OtlpExporterOptions"/>, whose <c>Endpoint</c> setter rejects the null a blank config
    /// value binds to, crashing the host from inside the binder before anything reads <c>Enabled</c>.
    /// </summary>
    public sealed class OtlpSettings
    {
        private Uri? _endpoint;
        private string? _headers;

        /// <summary>Unusable values normalise to null and are reported by
        /// <c>ThrowIfEndpointsMalformed</c>; blank is the silent opt-out.</summary>
        public Uri? Endpoint
        {
            get => _endpoint;
            set
            {
                _endpoint = OtlpEnvironment.SanitizeEndpoint(value);
                // A whitespace config value binds to a relative Uri, which is still the blank opt-out.
                RejectedEndpoint = _endpoint == null
                    ? OtlpEnvironment.Blank(value?.OriginalString)
                    : null;
            }
        }

        public string? Headers
        {
            get => _headers;
            set => _headers = OtlpEnvironment.Blank(value);
        }

        /// <summary>Null leaves the environment and SDK defaults in place.</summary>
        public OtlpExportProtocol? Protocol { get; set; }

        internal string? RejectedEndpoint { get; private set; }

        /// <summary>Writes the configured values onto <paramref name="target"/>, leaving anything
        /// unset at the exporter's own default rather than overwriting it.</summary>
        public void ApplyTo(OtlpExporterOptions target)
        {
            ArgumentNullException.ThrowIfNull(target);

            if (_endpoint != null) target.Endpoint = _endpoint;
            if (_headers != null) target.Headers = _headers;
            if (Protocol.HasValue) target.Protocol = Protocol.Value;
        }

    }
}
#endif
