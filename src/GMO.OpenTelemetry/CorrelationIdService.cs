using System;
using System.Diagnostics;

#if NET48
using System.Runtime.Remoting.Messaging;
using System.Web;
#endif

namespace GMO.OpenTelemetry
{
    public interface ICorrelationIdService
    {
        string GetCorrelationId();
        void SetCorrelationId(string correlationId);
        string GenerateCorrelationId();
#if NET48
        string? ExtractCorrelationIdFromHeaders();
#endif
    }

    public class CorrelationIdService : ICorrelationIdService
    {
        private const string CorrelationIdKey = "CorrelationId";

#if NET48
        private const string CallContextKey = "CorrelationId_CallContext";

        private static readonly string[] CorrelationIdHeaders =
        [
            "X-Correlation-ID",
            "X-Request-ID",
            "Request-ID",
            "Correlation-ID"
        ];
#else
        private static readonly AsyncLocal<string?> _correlationId = new();
#endif

        public string GetCorrelationId()
        {
            var fromActivity = GetCorrelationIdFromActivity(Activity.Current);
            if (!Strings.IsNullOrEmpty(fromActivity))
                return fromActivity;

#if NET48
            var fromContext = GetCorrelationIdFromCallContextOrHttp();
            if (!Strings.IsNullOrEmpty(fromContext))
                return fromContext;
#else
            if (!Strings.IsNullOrWhiteSpace(_correlationId.Value))
                return _correlationId.Value;
#endif

            var newCorrelationId = GenerateCorrelationId();
            SetCorrelationId(newCorrelationId);
            return newCorrelationId;
        }

        private static string? GetCorrelationIdFromActivity(Activity? activity)
        {
            if (activity == null)
                return null;

            var baggage = activity.GetBaggageItem(CorrelationIdKey);
            if (!string.IsNullOrEmpty(baggage))
                return baggage;

            foreach (var tag in activity.Tags)
            {
                if (tag.Key == CorrelationIdKey && !string.IsNullOrEmpty(tag.Value))
                    return tag.Value;
            }

            return GetCorrelationIdFromParentChain(activity.Parent);
        }

        private static string? GetCorrelationIdFromParentChain(Activity? parent)
        {
            const int maxDepth = 50;
            var current = parent;
            var depth = 0;
            while (current != null && depth < maxDepth)
            {
                var parentBaggage = current.GetBaggageItem(CorrelationIdKey);
                if (!string.IsNullOrEmpty(parentBaggage))
                    return parentBaggage;

                foreach (var tag in current.Tags)
                {
                    if (tag.Key == CorrelationIdKey && !string.IsNullOrEmpty(tag.Value))
                        return tag.Value;
                }

                current = current.Parent;
                depth++;
            }

            return null;
        }

#if NET48
        private string? GetCorrelationIdFromCallContextOrHttp()
        {
            var callContextValue = CallContext.LogicalGetData(CallContextKey) as string;
            if (!Strings.IsNullOrEmpty(callContextValue))
                return callContextValue;

            if (HttpContext.Current != null)
            {
                if (HttpContext.Current.Items[CorrelationIdKey] is string httpContextValue &&
                    !string.IsNullOrEmpty(httpContextValue))
                    return httpContextValue;

                var headerCorrelationId = ExtractCorrelationIdFromHeaders();
                if (!Strings.IsNullOrEmpty(headerCorrelationId))
                {
                    HttpContext.Current.Items[CorrelationIdKey] = headerCorrelationId;
                    return headerCorrelationId;
                }
            }

            return null;
        }
#endif

        public void SetCorrelationId(string correlationId)
        {
            if (string.IsNullOrWhiteSpace(correlationId))
            {
                correlationId = GenerateCorrelationId();
            }

            // Set in Activity if available
            var activity = Activity.Current;
            if (activity != null)
            {
                activity.AddTag(CorrelationIdKey, correlationId);
                activity.AddBaggage(CorrelationIdKey, correlationId);
            }

#if NET48
            // Set in CallContext for async flow
            CallContext.LogicalSetData(CallContextKey, correlationId);

            HttpContext.Current?.Items[CorrelationIdKey] = correlationId;
#else
            // Set in AsyncLocal storage (.NET 8+)
            _correlationId.Value = correlationId;
#endif
        }

        public string GenerateCorrelationId()
        {
            return Guid.NewGuid().ToString("D");
        }

#if NET48
        public string? ExtractCorrelationIdFromHeaders()
        {
            try
            {
                var httpContext = HttpContext.Current;
                if (httpContext?.Request?.Headers == null)
                    return null;

                foreach (var headerName in CorrelationIdHeaders)
                {
                    var headerValue = httpContext.Request.Headers[headerName];
                    if (!string.IsNullOrWhiteSpace(headerValue))
                        return headerValue;
                }
            }
            catch
            {
                // Silently ignore header parsing errors
            }

            return null;
        }
#endif
    }
}

