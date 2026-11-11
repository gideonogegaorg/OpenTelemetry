using System;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;

namespace GMO.OpenTelemetry
{
    public class CorrelationIdProcessor(IServiceProvider serviceProvider) : BaseProcessor<Activity>
    {
        private const string CorrelationIdKey = "CorrelationId";
        private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

        public override void OnStart(Activity activity)
        {
            try
            {
                if (activity.GetTagItem(CorrelationIdKey) != null)
                    return;

                var correlationId = GetCorrelationIdFromParent(activity);

                if (!string.IsNullOrEmpty(correlationId))
                {
                    activity.SetTag(CorrelationIdKey, correlationId);
                    return;
                }

                correlationId = Baggage.GetBaggage(CorrelationIdKey);

                if (!string.IsNullOrEmpty(correlationId))
                {
                    activity.SetTag(CorrelationIdKey, correlationId);
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var correlationIdService = scope.ServiceProvider.GetService<ICorrelationIdService>();

                if (correlationIdService != null)
                {
                    correlationId = correlationIdService.GetCorrelationId();
                    if (!string.IsNullOrEmpty(correlationId))
                        activity.SetTag(CorrelationIdKey, correlationId);
                }
            }
            catch
            {
                // Silently ignore errors in processor to avoid breaking telemetry
            }
        }

        private static string? GetCorrelationIdFromParent(Activity activity)
        {
            const int maxDepth = 50;
            var current = activity.Parent;
            var depth = 0;
            while (current != null && depth < maxDepth)
            {
                var correlationId = current.GetTagItem(CorrelationIdKey)?.ToString();
                if (!string.IsNullOrEmpty(correlationId))
                    return correlationId;
                current = current.Parent;
                depth++;
            }

            return null;
        }
    }
}
