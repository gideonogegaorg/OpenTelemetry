using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace GMO.OpenTelemetry
{
    public static class ActivityExtensions
    {
        public static Activity? GetRoot(Action<Activity>? act = null)
        {
            return GetRoot(Activity.Current, act);
        }

        public static Activity? GetRoot(this Activity? activity, Action<Activity>? act = null)
        {
            const int maxDepth = 50;
            var depth = 0;
            while (activity != null && activity.Parent != null && depth < maxDepth)
            {
                activity = activity.Parent;
                depth++;
            }

            if (activity != null) act?.Invoke(activity);

            return activity;
        }

    }
}
