using System;
using System.Diagnostics;

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
            while (activity != null && activity.Parent != null) activity = activity.Parent;

            if (activity != null) act?.Invoke(activity);

            return activity;
        }
    }
}
