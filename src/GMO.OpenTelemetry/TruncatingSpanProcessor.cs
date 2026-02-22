using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

namespace GMO.OpenTelemetry
{
    public class TruncatingSpanProcessor : BaseProcessor<Activity>
    {
        private static readonly Regex TenantRegex = new Regex(
                @"^(?=.{1,63}$)[a-z0-9]([-a-z0-9]*[a-z0-9])?$",
                RegexOptions.Compiled
        );

        private const string NewRelicIngestUrl = "https://log-api.newrelic.com/log/v1";
        private const string DynatraceIngestUrl = "https://{0}.live.dynatrace.com/api/v2/logs/ingest";

        private static readonly string OtelLibraryName = typeof(TruncatingSpanProcessor).FullName ?? "GMO.OpenTelemetry.TruncatingSpanProcessor";
        private static readonly string OtelLibraryVersion = typeof(TruncatingSpanProcessor).Assembly.GetName().Version?.ToString() ?? "unknown";

        private readonly HashSet<string> _keysToLog;
        private readonly HttpClient _httpClient;
        private readonly IOpenTelemetryOptions _options;
        private readonly IAttributeEnricher _attributeEnricher;
        private readonly string _apiKey;

        public TruncatingSpanProcessor(
            IOpenTelemetryOptions options,
            IAttributeEnricher attributeEnricher)
        {
            if (!options.Enabled) return;

            _options = options;
            _attributeEnricher = attributeEnricher;
            _apiKey = _options.GetApiKey() ?? throw new ArgumentNullException($"Null or invalid open telemety api key");

            _httpClient = new HttpClient();
            if (_options.ServiceProvider == OpenTelemetryServiceProvider.NewRelic)
            {
                _httpClient.BaseAddress = new Uri(NewRelicIngestUrl);
                _httpClient.DefaultRequestHeaders.Add("Api-Key", _apiKey);
            }
            else
            {
                var endpoint = options.GetOtlpEndpoint() ?? throw new ArgumentException($"Null or invalid open telemetry endpoint");
                var tenant = endpoint.Host.Split('.')[0];
                if (!TenantRegex.IsMatch(tenant)) throw new ArgumentException($"Invalid tenant identifier {tenant}");

                _httpClient.BaseAddress = new Uri(string.Format(DynatraceIngestUrl, tenant));
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Api-Token {_apiKey}");
            }

            _keysToLog = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase) { "db.statement", "db.params" };
            if (_options.AdditionalKeysToLog != null)
            {
                foreach (var key in _options.AdditionalKeysToLog)
                {
                    _keysToLog.Add(key);
                }
            }
        }

        public override void OnEnd(Activity activity)
        {
            if (activity == null || !_options.Enabled || !_options.EnableSqlInstrumentation) return;

            var rootName = ActivityExtensions.GetRoot()?.DisplayName?.ToUpperInvariant() ?? "unknown";
            var shouldEmitLog = _options.EnableSqlInstrumentationText;
            var shouldSendOverflow = _options.SendOverflowLogs
                    && (_options.OverflowLogOperations.Count == 0 // No operation filters
                    || _options.OverflowLogOperations.Contains(rootName));

            foreach (var tag in activity.Tags.ToList())
            {
                if (shouldEmitLog && (_keysToLog.Contains(tag.Key) && tag.Value is string raw && raw.Length > _options.MaxAttributeValueLength))
                {
                    _ = EmitTraceLogAsync(activity, tag.Key, raw, shouldSendOverflow);

                    activity.SetTag(tag.Key, raw.Substring(0, _options.MaxAttributeValueLength));
                    activity.SetTag($"{tag.Key}Truncated", true);
                }
                else if (tag.Value is string other && other.Length > _options.MaxAttributeValueLength)
                {
                    activity.SetTag(tag.Key, other.Substring(0, _options.MaxAttributeValueLength));
                }
            }
        }

        private async Task EmitTraceLogAsync(Activity activity, string key, string raw, bool shouldSendOverflow)
        {
            if (string.IsNullOrEmpty(_apiKey)) return;

            var wasOverflow = raw.Length > _options.MaxLogMessageLength;
            var totalChunks = (int)Math.Ceiling(raw.Length / (double)_options.MaxLogMessageLength);
            int chunkIndex = 0;

            do
            {
                var start = chunkIndex * _options.MaxLogMessageLength;
                var length = Math.Min(_options.MaxLogMessageLength, raw.Length - start);
                var chunk = raw.Substring(start, length);

                // Use the shared enrichment service
                var attrs = _attributeEnricher.Enrich(activity: activity);

                attrs["otel.library.name"] = OtelLibraryName;
                attrs["otel.library.version"] = OtelLibraryVersion;

                // Add specific attributes for this use case
                attrs["attribute.key"] = key;
                attrs["overflow"] = wasOverflow;
                attrs["chunk.index"] = chunkIndex + 1;
                attrs["chunk.count"] = totalChunks;
                attrs["level"] = LogLevel.Information.ToString();

                var payloadKey = _options.ServiceProvider == OpenTelemetryServiceProvider.Dynatrace ? "content" : "message";
                var logEvent = new Dictionary<string, object>
                {
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["attributes"] = attrs,
                    [payloadKey] = chunk
                };

                var payload = JsonSerializer.Serialize(new[] { logEvent });

                using (var content = new StringContent(payload, Encoding.UTF8, "application/json"))
                {
                    try
                    {
                        await _httpClient.PostAsync(string.Empty, content).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }

                chunkIndex++;
            }
            while (shouldSendOverflow && chunkIndex < totalChunks);
        }
    }
}
