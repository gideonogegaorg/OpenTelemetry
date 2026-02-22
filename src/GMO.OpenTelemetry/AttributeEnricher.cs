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

    public class AttributeEnricher : IAttributeEnricher
    {
        private const string CorrelationIdKey = "CorrelationId";
        private readonly ICorrelationIdService? _correlationIdService;
        private readonly IOpenTelemetryOptions _options;
        private readonly string[] _logExclusions;

        private static readonly string[] DefaultLogExclusions = new[]
        {
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
        };

        public AttributeEnricher(
            IOpenTelemetryOptions options,
            ICorrelationIdService? correlationIdService = null)
        {
            _correlationIdService = correlationIdService;
            _options = options;

            _logExclusions = DefaultLogExclusions
                .Concat(options?.LogStackExclusions ?? new string[0])
                .ToArray();
        }

        public Dictionary<string, object> Enrich(Activity? activity = null)
        {
            var attributes = new Dictionary<string, object>(_options?.Attributes ?? new Dictionary<string, object>());

            // Add OpenTelemetry trace context
            if (activity == null)
                activity = Activity.Current;

            if (activity != null)
            {
                attributes["trace.id"] = activity.TraceId.ToString();
                attributes["span.id"] = activity.SpanId.ToString();
                attributes["operation.name"] = activity.GetRoot()?.DisplayName ?? string.Empty;

                var correlationId = GetCorrelationId(activity);
                if (!string.IsNullOrEmpty(correlationId))
                {
                    attributes[CorrelationIdKey] = correlationId;
                }
            }

            // Add thread information
            if (_options!.IncludeThreadInfo)
            {
                attributes["thread.id"] = System.Threading.Thread.CurrentThread.ManagedThreadId.ToString();
                attributes["thread.name"] = System.Threading.Thread.CurrentThread.Name ?? "Unknown";
            }

            // Add user identity
            if (_options.IncludeUserInfo)
            {
                var userIdentity = GetUserIdentity();
                if (!string.IsNullOrEmpty(userIdentity))
                {
                    attributes["user.identity"] = userIdentity;
                }

                var appDomain = GetAppDomain();
                if (!string.IsNullOrEmpty(appDomain))
                {
                    attributes["app.domain"] = appDomain;
                }
            }

            // Add source location information
            if (_options.IncludeSourceLocation)
            {
                AddSourceLocationAttributes(attributes);
            }

            return attributes;
        }

        public string? GetCorrelationId(Activity? activity = null)
        {
            return _correlationIdService?.GetCorrelationId();
        }

        private string? GetUserIdentity()
        {
            try
            {
                var currentPrincipal = System.Threading.Thread.CurrentPrincipal;
                if (currentPrincipal?.Identity != null && !string.IsNullOrEmpty(currentPrincipal.Identity.Name))
                {
                    return currentPrincipal.Identity.Name;
                }

                var environmentUser = System.Environment.UserName;
                if (!string.IsNullOrEmpty(environmentUser))
                {
                    return environmentUser;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private string? GetAppDomain()
        {
            try
            {
                return System.AppDomain.CurrentDomain?.FriendlyName;
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
                var stackTrace = new StackTrace(true);
                StackFrame? sourceFrame = null;

                for (int i = 0; i < stackTrace.FrameCount; i++)
                {
                    var frame = stackTrace.GetFrame(i);
                    var method = frame?.GetMethod();

                    if (method != null)
                    {
                        var declaringType = method.DeclaringType;
                        var fullTypeName = declaringType?.FullName ?? string.Empty;

                        if (!ExcludeNamespaceOrType(fullTypeName))
                        {
                            sourceFrame = frame;
                            break;
                        }
                    }
                }

                if (sourceFrame != null)
                {
                    var method = sourceFrame.GetMethod();

                    var className = method?.DeclaringType?.FullName;
                    if (!string.IsNullOrEmpty(className))
                        attributes["source.class"] = className;

                    var methodName = method?.Name;
                    if (!string.IsNullOrEmpty(methodName))
                        attributes["source.method"] = methodName;

                    var fileName = sourceFrame.GetFileName();
                    if (!string.IsNullOrEmpty(fileName))
                        attributes["source.file"] = fileName;

                    var lineNumber = sourceFrame.GetFileLineNumber();
                    if (lineNumber > 0)
                        attributes["source.line"] = lineNumber.ToString();
                }
            }
            catch
            {
                // Silently ignore stack trace errors
            }
        }

        private bool ExcludeNamespaceOrType(string fullTypeName)
        {
            return _logExclusions.Any(le => fullTypeName.StartsWith(le, StringComparison.OrdinalIgnoreCase));
        }
    }
}
