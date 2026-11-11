using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace GMO.OpenTelemetry
{
    public interface IAttributeEnricher
    {
        Dictionary<string, object> Enrich(Activity? activity = null);

        string? GetCorrelationId(Activity? activity = null);
    }

    public class AttributeEnricher(
        IOpenTelemetryOptions options,
        ICorrelationIdService? correlationIdService = null) : IAttributeEnricher
    {
        private const string CorrelationIdKey = "CorrelationId";
        private readonly ICorrelationIdService? _correlationIdService = correlationIdService;
        private readonly IOpenTelemetryOptions? _options = options;
        private readonly string[] _logExclusions = [.. DefaultLogExclusions, .. options?.LogStackExclusions ?? []];

        private static readonly string[] DefaultLogExclusions =
        [
            "System",
            "Microsoft",
            "OpenTelemetry",
            "Serilog",
            "Hangfire",
            "GMO.OpenTelemetry",
            "GMO.OpenTelemetry.ActivityWrapper",
            "GMO.OpenTelemetry.RootActivityWrapper",
            "GMO.OpenTelemetry.ActivityWrapper",
            "GMO.OpenTelemetry.RootActivityWrapper",
            "GMO.Utils.Maybe",
            "GMO.TaskEngine.Core.Filters.LoggingFilter",
            "GMO.Logging.LegacyLogWrapper"
        ];

        public Dictionary<string, object> Enrich(Activity? activity = null)
        {
            try
            {
                var attributes = new Dictionary<string, object>(_options?.Attributes ?? []);

                activity ??= Activity.Current;

                if (activity != null)
                {
                    attributes["trace.id"] = activity.TraceId.ToString();
                    attributes["span.id"] = activity.SpanId.ToString();
                    attributes["operation.name"] = activity.GetRoot()?.DisplayName ?? string.Empty;

                    var correlationId = GetCorrelationId(activity);
                    if (!Strings.IsNullOrEmpty(correlationId))
                    {
                        attributes[CorrelationIdKey] = correlationId;
                    }
                }

                if (_options?.IncludeThreadInfo == true)
                {
                    attributes["thread.id"] = Environment.CurrentManagedThreadId.ToString();
                    attributes["thread.name"] = System.Threading.Thread.CurrentThread.Name ?? "Unknown";
                }

                if (_options?.IncludeUserInfo == true)
                {
                    var userIdentity = GetUserIdentity();
                    if (!Strings.IsNullOrEmpty(userIdentity))
                        attributes["user.identity"] = userIdentity;

                    var appDomain = GetAppDomain();
                    if (!Strings.IsNullOrEmpty(appDomain))
                        attributes["app.domain"] = appDomain;
                }

                if (_options?.IncludeSourceLocation == true)
                    AddSourceLocationAttributes(attributes);

                return attributes;
            }
            catch
            {
                return [];
            }
        }

        public string? GetCorrelationId(Activity? activity = null)
        {
            return _correlationIdService?.GetCorrelationId();
        }

        private static string? GetUserIdentity()
        {
            try
            {
                var currentPrincipal = System.Threading.Thread.CurrentPrincipal;
                if (currentPrincipal?.Identity != null && !string.IsNullOrEmpty(currentPrincipal.Identity.Name))
                    return currentPrincipal.Identity.Name;

                var environmentUser = System.Environment.UserName;
                return string.IsNullOrEmpty(environmentUser) ? null : environmentUser;
            }
            catch
            {
                return null;
            }
        }

        private static string? GetAppDomain()
        {
            try
            {
                return System.AppDomain.CurrentDomain.FriendlyName;
            }
            catch
            {
                return null;
            }
        }

        private void AddSourceLocationAttributes(Dictionary<string, object> attributes)
        {
            try
            {
                var sourceFrame = TryGetSourceFrame(new StackTrace(true));
                if (sourceFrame == null) return;

                var method = sourceFrame.GetMethod();

                var className = method?.DeclaringType?.FullName;
                if (!Strings.IsNullOrEmpty(className))
                    attributes["source.class"] = className;

                var methodName = method?.Name;
                if (!Strings.IsNullOrEmpty(methodName))
                    attributes["source.method"] = methodName;

                var fileName = sourceFrame.GetFileName();
                if (!Strings.IsNullOrEmpty(fileName))
                    attributes["source.file"] = fileName;

                var lineNumber = sourceFrame.GetFileLineNumber();
                if (lineNumber > 0)
                    attributes["source.line"] = lineNumber.ToString();
            }
            catch
            {
                // Silently ignore stack trace errors
            }
        }

        private StackFrame? TryGetSourceFrame(StackTrace stackTrace)
        {
            for (int i = 0; i < stackTrace.FrameCount; i++)
            {
                var frame = stackTrace.GetFrame(i);
                var method = frame?.GetMethod();
                if (method == null) continue;

                var fullTypeName = method.DeclaringType?.FullName ?? string.Empty;
                if (!ExcludeNamespaceOrType(fullTypeName))
                    return frame;
            }

            return null;
        }

        private bool ExcludeNamespaceOrType(string fullTypeName)
        {
            return _logExclusions.Any(le => fullTypeName.StartsWith(le, StringComparison.OrdinalIgnoreCase));
        }
    }
}
