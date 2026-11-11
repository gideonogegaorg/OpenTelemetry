using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

namespace GMO.OpenTelemetry
{
    public class TruncatingSpanProcessor : BaseProcessor<Activity>
    {
        private static readonly Regex TenantRegex = new Regex(
                @"^(?=.{1,63}$)[a-z0-9]([-a-z0-9]*[a-z0-9])?$",
                RegexOptions.Compiled,
                TimeSpan.FromSeconds(1)
        );

        private const string NewRelicIngestUrl = "https://log-api.newrelic.com/log/v1"; // NOSONAR - well-known fixed API endpoint
        private const string DynatraceIngestUrl = "https://{0}.live.dynatrace.com/api/v2/logs/ingest"; // NOSONAR - well-known fixed API endpoint
        private const string DynatraceOtlpApiPath = "/api/v2/otlp";
        private const string DynatraceLogsApiPath = "/api/v2/logs/ingest";

        private static readonly string OtelLibraryName = typeof(TruncatingSpanProcessor).FullName ?? nameof(TruncatingSpanProcessor);
        private static readonly string OtelLibraryVersion = typeof(TruncatingSpanProcessor).Assembly.GetName().Version?.ToString() ?? "unknown";

        private readonly HashSet<string> _keysToLog = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
        private readonly IOpenTelemetryOptions _options;
        private readonly IAttributeEnricher _attributeEnricher;
        private readonly HttpClient? _httpClient;
        private readonly string? _apiKey;
        private readonly bool _initialized;

        private readonly Channel<OverflowLogEntry>? _overflowChannel;
        private readonly int _channelCapacity;
        private readonly int _maxAttributeValueLength;
        private readonly int _maxChunkLength;
        private int _saturationReported;
        private int _disposed;
        private readonly CancellationTokenSource? _cts;
        private readonly Task? _consumerTask;
        private DateTime _lastFailureUtc = DateTime.MinValue;
        private static readonly TimeSpan _cooldownPeriod = TimeSpan.FromSeconds(2);

        public TruncatingSpanProcessor(
            IOpenTelemetryOptions options,
            IAttributeEnricher attributeEnricher,
            HttpMessageHandler? httpMessageHandler = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _attributeEnricher = attributeEnricher ?? throw new ArgumentNullException(nameof(attributeEnricher));

            if (!options.Enabled)
            {
                _initialized = false;
                return;
            }

            // Both limits are validated once here rather than per span: a misconfiguration is static, so
            // re-deciding it on the hot path only produced an unbounded Degraded flood.
            _maxAttributeValueLength = _options.MaxAttributeValueLength;
            if (_maxAttributeValueLength <= 0)
            {
                GmoTelemetryEventSource.Log.Degraded(nameof(TruncatingSpanProcessor),
                    $"MaxAttributeValueLength is not positive ({_maxAttributeValueLength}); attribute truncation disabled");
            }

            _maxChunkLength = _options.MaxLogMessageLength;
            if (_maxChunkLength <= 0)
            {
                _maxChunkLength = OpenTelemetryOptionsBase.DefaultMaxLogMessageLength;
                GmoTelemetryEventSource.Log.Degraded(nameof(TruncatingSpanProcessor),
                    $"MaxLogMessageLength is not positive; clamping overflow chunks to {_maxChunkLength}");
            }

            if (_options.EnableSqlInstrumentation)
            {
                _keysToLog.Add("db.statement");
                _keysToLog.Add("db.params");
            }

            if (_options.AdditionalKeysToLog != null)
            {
                foreach (var key in _options.AdditionalKeysToLog)
                {
                    _keysToLog.Add(key);
                }
            }

            _apiKey = _options.GetApiKey();
            var ingestUri = string.IsNullOrEmpty(_apiKey) ? null : ResolveOverflowIngestUri();
            if (ingestUri == null)
            {
                // Truncation is local, so it survives absent credentials; only the direct-to-vendor
                // overflow post is disabled, and EnqueueOverflowLog already no-ops without a key.
                // The handler would otherwise leak: nothing takes ownership on this path.
                httpMessageHandler?.Dispose();
                GmoTelemetryEventSource.Log.Degraded(nameof(TruncatingSpanProcessor),
                    "overflow log delivery disabled (no API key or usable ingest endpoint); attribute truncation remains active");
                _initialized = true;
                return;
            }

            _httpClient = httpMessageHandler != null
                ? new HttpClient(httpMessageHandler) { Timeout = TimeSpan.FromSeconds(10) }
                : new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _httpClient.BaseAddress = ingestUri;

            if (_options.ServiceProvider == OpenTelemetryServiceProvider.NewRelic)
                _httpClient.DefaultRequestHeaders.Add("Api-Key", _apiKey);
            else
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Api-Token {_apiKey}");

            _channelCapacity = Math.Max(1, _options.OverflowChannelCapacity);
            _overflowChannel = Channel.CreateBounded<OverflowLogEntry>(new BoundedChannelOptions(_channelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

            _cts = new CancellationTokenSource();
            _consumerTask = Task.Run(() => ConsumeOverflowLogsAsync(_cts.Token));
            _initialized = true;
        }

        /// <summary>Base address for the direct-to-vendor overflow post, or null when it cannot be
        /// derived - in which case overflow delivery is skipped rather than failing construction.</summary>
        private Uri? ResolveOverflowIngestUri()
        {
            if (_options.ServiceProvider == OpenTelemetryServiceProvider.NewRelic)
                return new Uri(NewRelicIngestUrl);

            // A collector has no vendor log API; the Dynatrace derivation below would turn
            // http://localhost:4318 into localhost.live.dynatrace.com and send the API key there.
            if (_options.ServiceProvider != OpenTelemetryServiceProvider.Dynatrace)
                return null;

            var endpoint = _options.GetOtlpEndpoint();
            if (endpoint == null)
                return null;

            // ActiveGate and Managed carry the tenant in the path (/e/{id}/api/v2/otlp) rather than the
            // host, so swapping the API segment preserves it. Rebuilding from the first host label would
            // aim a Managed deployment at a live.dynatrace.com tenant that is not theirs.
            var marker = endpoint.AbsolutePath.IndexOf(DynatraceOtlpApiPath, StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                return new UriBuilder(endpoint)
                {
                    // net48 has no span-based string.Concat overload, hence the split.
#if NET
                    Path = string.Concat(endpoint.AbsolutePath.AsSpan(0, marker), DynatraceLogsApiPath),
#else
                    Path = endpoint.AbsolutePath.Substring(0, marker) + DynatraceLogsApiPath,
#endif
                    Query = string.Empty,
                    Fragment = string.Empty,
                }.Uri;
            }

            var tenant = endpoint.Host.Split('.')[0];
            return TenantRegex.IsMatch(tenant)
                ? new Uri(string.Format(DynatraceIngestUrl, tenant))
                : null;
        }

        // New Relic rejects a span whose start is more than 24 hours old when it arrives, so a span open
        // that long - a host-lifetime root, a stuck job - is lost with nothing to show for it. The margin
        // covers the export delay and clock skew.
        internal static readonly TimeSpan MaxSpanAge = TimeSpan.FromHours(23);

        public override void OnEnd(Activity activity)
        {
            if (activity == null || !_initialized || !_options.Enabled) return;

            ReportIfTooOld(activity);

            if (_maxAttributeValueLength <= 0) return;

            TruncateStatusDescription(activity);

            var rootName = ActivityExtensions.GetRoot()?.DisplayName?.ToUpperInvariant() ?? "unknown";
            var shouldEmitLog = _options.EnableSqlInstrumentationText;
            var shouldSendOverflow = _options.SendOverflowLogs
                    && (_options.OverflowLogOperations.Count == 0
                    || _options.OverflowLogOperations.Contains(rootName));

            // Two-pass approach: collect modifications first, apply after iteration
            // to avoid allocating a full ToList() copy of all tags on every span
            List<(string Key, string Raw, bool EmitOverflow)>? modifications = null;

            foreach (var tag in activity.Tags)
            {
                try
                {
                    if (tag.Value is not string value || value.Length <= _maxAttributeValueLength)
                        continue;

                    var emitOverflow = shouldEmitLog && _keysToLog.Contains(tag.Key);
                    modifications ??= new List<(string, string, bool)>();
                    modifications.Add((tag.Key, value, emitOverflow));
                }
                catch (Exception ex)
                {
                    GmoTelemetryEventSource.Log.OperationFailed(nameof(TruncatingSpanProcessor),
                        $"processing tag '{tag.Key}'", GmoTelemetryEventSource.Describe(ex));
                }
            }

            if (modifications != null)
            {
                ApplyModifications(activity, modifications, shouldSendOverflow);
            }
        }

        private static void ReportIfTooOld(Activity activity)
        {
            var age = DateTime.UtcNow - activity.StartTimeUtc;
            if (age > MaxSpanAge)
            {
                GmoTelemetryEventSource.Log.ItemsDropped(nameof(TruncatingSpanProcessor), 1,
                    $"span '{activity.DisplayName}' started {age.TotalHours:F0} h ago; New Relic rejects spans that started more than 24 h before they arrive");
            }
        }

        // Instrumentation sets this from an exception message, which can run to any length.
        private void TruncateStatusDescription(Activity activity)
        {
            if (activity.StatusDescription != null && activity.StatusDescription.Length > _maxAttributeValueLength)
                activity.SetStatus(activity.Status, TruncateForTag(activity.StatusDescription));
        }

        private void ApplyModifications(Activity activity, List<(string Key, string Raw, bool EmitOverflow)> modifications, bool shouldSendOverflow)
        {
            foreach (var mod in modifications)
            {
                try
                {
                    if (mod.EmitOverflow)
                    {
                        if (shouldSendOverflow)
                            EnqueueOverflowLog(activity, mod.Key, mod.Raw);
                        activity.SetTag(mod.Key, TruncateForTag(mod.Raw));
                        activity.SetTag($"{mod.Key}Truncated", true);
                    }
                    else
                    {
                        activity.SetTag(mod.Key, TruncateForTag(mod.Raw));
                    }
                }
                catch (Exception ex)
                {
                    GmoTelemetryEventSource.Log.OperationFailed(nameof(TruncatingSpanProcessor),
                        $"applying truncation for tag '{mod.Key}'", GmoTelemetryEventSource.Describe(ex));
                }
            }
        }

        // Surrogate-safe: this value ships on the span, where a lone surrogate becomes U+FFFD or rejects
        // the record. Only reached with a positive limit and a value longer than it: OnEnd returns first
        // when the limit is unusable.
        private string TruncateForTag(string raw) =>
            raw.Substring(0, LogMessageChunker.FirstChunkLength(raw, _maxAttributeValueLength));

        private void EnqueueOverflowLog(Activity activity, string key, string raw)
        {
            if (_overflowChannel == null || string.IsNullOrEmpty(_apiKey)) return;

            try
            {
                // A non-positive limit means "no chunking" to the Serilog sink, but here it would post the
                // whole raw attribute, and PostWithSplitAsync cannot split a single entry to recover.
                var wasOverflow = raw.Length > _maxChunkLength;
                var chunks = LogMessageChunker.Chunk(raw, _maxChunkLength);

                for (int chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
                {
                    var chunk = chunks[chunkIndex];

                    var attrs = _attributeEnricher.Enrich(activity: activity);
                    attrs["otel.library.name"] = OtelLibraryName;
                    attrs["otel.library.version"] = OtelLibraryVersion;
                    attrs["attribute.key"] = key;
                    attrs["overflow"] = wasOverflow;
                    attrs["chunk.index"] = chunkIndex + 1;
                    attrs["chunk.count"] = chunks.Count;
                    attrs["level"] = LogLevel.Information.ToString();

                    var payloadKey = _options.ServiceProvider == OpenTelemetryServiceProvider.Dynatrace ? "content" : "message";
                    var entry = new OverflowLogEntry(attrs, payloadKey, chunk);

                    // DropOldest means TryWrite always succeeds, so its result cannot reveal a drop.
                    _overflowChannel.Writer.TryWrite(entry);
                }

                ReportIfSaturated();
            }
            catch (Exception ex)
            {
                GmoTelemetryEventSource.Log.OperationFailed(nameof(TruncatingSpanProcessor),
                    "enqueueing overflow log", GmoTelemetryEventSource.Describe(ex));
            }
        }

        // Degraded, not ItemsDropped: DropOldest cannot count evictions. Latched to report the
        // transition once, not once per enqueue.
        private void ReportIfSaturated()
        {
            var saturated = _overflowChannel!.Reader.Count >= _channelCapacity;
            if (saturated && Interlocked.Exchange(ref _saturationReported, 1) == 0)
            {
                GmoTelemetryEventSource.Log.Degraded(nameof(TruncatingSpanProcessor),
                    $"overflow channel at capacity ({_channelCapacity}); oldest entries are being evicted");
            }
            else if (!saturated)
            {
                Interlocked.Exchange(ref _saturationReported, 0);
            }
        }

        private async Task ConsumeOverflowLogsAsync(CancellationToken ct)
        {
            var batchSize = Math.Max(1, _options.OverflowBatchSize);
            var flushInterval = TimeSpan.FromMilliseconds(Math.Max(100, _options.OverflowBatchFlushIntervalMs));
            var batch = new List<OverflowLogEntry>(batchSize);

            try
            {
                while (await _overflowChannel!.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    // Wait out the cooldown before draining. Entries read during one would be discarded
                    // by FlushBatchAsync and lost outright; entries left in the channel survive it.
                    var cooldownRemaining = _cooldownPeriod - (DateTime.UtcNow - _lastFailureUtc);
                    if (cooldownRemaining > TimeSpan.Zero)
                    {
                        await Task.Delay(cooldownRemaining, ct).ConfigureAwait(false);
                        continue;
                    }

                    ReadAvailableEntries(batch, batchSize);
                    await WaitForBatchOrFlushInterval(batch, batchSize, flushInterval, ct).ConfigureAwait(false);

                    if (batch.Count > 0)
                    {
                        await FlushBatchAsync(batch).ConfigureAwait(false);
                        batch.Clear();
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await DrainRemainingEntries(batch, batchSize).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GmoTelemetryEventSource.Log.OperationFailed(nameof(TruncatingSpanProcessor),
                    "consumer loop", ex.ToString());
            }
        }

        private void ReadAvailableEntries(List<OverflowLogEntry> batch, int batchSize)
        {
            while (batch.Count < batchSize && _overflowChannel!.Reader.TryRead(out var entry))
            {
                batch.Add(entry);
            }
        }

        private async Task WaitForBatchOrFlushInterval(List<OverflowLogEntry> batch, int batchSize, TimeSpan flushInterval, CancellationToken ct)
        {
            if (batch.Count >= batchSize) return;

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            delayCts.CancelAfter(flushInterval);

            try
            {
                while (batch.Count < batchSize &&
                       await _overflowChannel!.Reader.WaitToReadAsync(delayCts.Token).ConfigureAwait(false))
                {
                    ReadAvailableEntries(batch, batchSize);
                }
            }
            catch (OperationCanceledException) when (delayCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Flush interval elapsed, send what we have
            }
        }

        // Drains onto the pending batch rather than clearing it first: cancellation usually arrives while
        // the loop is waiting out its flush interval, with entries already read out of the channel. Those
        // exist nowhere else, so clearing would discard them at exactly the point nothing can re-read
        // them. The consumer loop clears only after a flush, so anything still here is undelivered.
        private async Task DrainRemainingEntries(List<OverflowLogEntry> batch, int batchSize)
        {
            while (_overflowChannel!.Reader.TryRead(out var entry))
            {
                batch.Add(entry);
                if (batch.Count >= batchSize)
                {
                    await FlushBatchAsync(batch).ConfigureAwait(false);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await FlushBatchAsync(batch).ConfigureAwait(false);
                batch.Clear();
            }
        }

        private async Task FlushBatchAsync(List<OverflowLogEntry> batch)
        {
            // Cooldown: skip if last attempt failed within the cooldown period. Under sustained
            // backpressure this discards more than the exception path below, so it must report too.
            if (DateTime.UtcNow - _lastFailureUtc < _cooldownPeriod)
            {
                GmoTelemetryEventSource.Log.ItemsDropped(nameof(TruncatingSpanProcessor), batch.Count,
                    "flush skipped during failure cooldown");
                return;
            }

            try
            {
                var logEvents = new List<Dictionary<string, object>>(batch.Count);
                foreach (var entry in batch)
                {
                    logEvents.Add(new Dictionary<string, object>
                    {
                        ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        ["attributes"] = entry.Attributes,
                        [entry.PayloadKey] = entry.Chunk
                    });
                }

                await PostWithSplitAsync(logEvents).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _lastFailureUtc = DateTime.UtcNow;
                GmoTelemetryEventSource.Log.ItemsDropped(nameof(TruncatingSpanProcessor), batch.Count,
                    "batch flush failed: " + ex);
            }
        }

        private async Task PostWithSplitAsync(List<Dictionary<string, object>> logEvents)
        {
            var payloadBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(logEvents));
            var maxBytes = Math.Max(1024, _options.OverflowMaxPayloadBytes);

            if (payloadBytes.Length <= maxBytes || logEvents.Count <= 1)
            {
                await PostPayloadAsync(payloadBytes).ConfigureAwait(false);
                return;
            }

            var mid = logEvents.Count / 2;
            await PostWithSplitAsync(logEvents.GetRange(0, mid)).ConfigureAwait(false);
            await PostWithSplitAsync(logEvents.GetRange(mid, logEvents.Count - mid)).ConfigureAwait(false);
        }

        private async Task PostPayloadAsync(byte[] payloadBytes)
        {
            using var compressedStream = new MemoryStream();
            using (var gzipStream = new GZipStream(compressedStream, CompressionLevel.Fastest, leaveOpen: true))
            {
#if NETFRAMEWORK
                await gzipStream.WriteAsync(payloadBytes, 0, payloadBytes.Length).ConfigureAwait(false);
#else
                await gzipStream.WriteAsync(payloadBytes).ConfigureAwait(false);
#endif
            }

            compressedStream.Position = 0;
            using var content = new StreamContent(compressedStream);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            content.Headers.ContentEncoding.Add("gzip");

            using var response = await _httpClient!.PostAsync(string.Empty, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                GmoTelemetryEventSource.Log.ExportRejected(nameof(TruncatingSpanProcessor),
                    (int)response.StatusCode, _httpClient!.BaseAddress?.ToString() ?? "overflow log endpoint");

                // Throwing lets FlushBatchAsync count the loss once, and unwinds any remaining split
                // halves rather than firing them at an endpoint that just refused us.
                throw new HttpRequestException(
                    $"overflow log export rejected with HTTP {(int)response.StatusCode}");
            }
        }

        protected override void Dispose(bool disposing)
        {
            // The container and the TracerProvider both dispose the processor, and BaseProcessor does
            // not guard it: a second pass cancels a disposed CancellationTokenSource.
            if (disposing && _initialized && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    _overflowChannel?.Writer.TryComplete();
                    _cts?.Cancel();

                    // Allow consumer to drain for a short period
                    _consumerTask?.Wait(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    GmoTelemetryEventSource.Log.OperationFailed(nameof(TruncatingSpanProcessor),
                        "dispose", ex.ToString());
                }
                finally
                {
                    _cts?.Dispose();
                    _httpClient?.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        private readonly struct OverflowLogEntry
        {
            public Dictionary<string, object> Attributes { get; }
            public string PayloadKey { get; }
            public string Chunk { get; }

            public OverflowLogEntry(Dictionary<string, object> attributes, string payloadKey, string chunk)
            {
                Attributes = attributes;
                PayloadKey = payloadKey;
                Chunk = chunk;
            }
        }
    }
}
