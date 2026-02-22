using System;
using System.Diagnostics;

namespace GMO.OpenTelemetry
{
    public interface ICorrelationIdService
    {
        string GetCorrelationId();
        void SetCorrelationId(string correlationId);
        string GenerateCorrelationId();
    }

    public class CorrelationIdService : ICorrelationIdService
    {
        private const string CorrelationIdKey = "CorrelationId";

        private static readonly AsyncLocal<string?> _correlationId = new();

        public string GetCorrelationId()
        {
            // First try to get from Activity (OpenTelemetry)
            var activity = Activity.Current;
            if (activity != null)
            {
                // Check baggage first
                var baggageCorrelationId = activity.GetBaggageItem(CorrelationIdKey);
                if (!string.IsNullOrEmpty(baggageCorrelationId))
                    return baggageCorrelationId;

                // Check tags
                foreach (var tag in activity.Tags)
                {
                    if (tag.Key == CorrelationIdKey && !string.IsNullOrEmpty(tag.Value))
                        return tag.Value;
                }

                // Walk up the activity tree
                var currentActivity = activity.Parent;
                while (currentActivity != null)
                {
                    var parentBaggage = currentActivity.GetBaggageItem(CorrelationIdKey);
                    if (!string.IsNullOrEmpty(parentBaggage))
                        return parentBaggage;

                    foreach (var tag in currentActivity.Tags)
                    {
                        if (tag.Key == CorrelationIdKey && !string.IsNullOrEmpty(tag.Value))
                            return tag.Value;
                    }

                    currentActivity = currentActivity.Parent;
                }
            }

            // Check AsyncLocal storage
            if (!string.IsNullOrWhiteSpace(_correlationId.Value))
            {
                return _correlationId.Value;
            }

            // Generate new one if none found
            var newCorrelationId = GenerateCorrelationId();
            SetCorrelationId(newCorrelationId);
            return newCorrelationId;
        }

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

            _correlationId.Value = correlationId;
        }

        public string GenerateCorrelationId()
        {
            return Guid.NewGuid().ToString("D");
        }
    }
}
