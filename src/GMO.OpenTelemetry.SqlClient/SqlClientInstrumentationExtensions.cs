using System.Data.Common;
using System.Diagnostics;
using OpenTelemetry.Trace;

namespace GMO.OpenTelemetry.SqlClient
{
    public static class SqlClientInstrumentationExtensions
    {
        public static TracerProviderBuilder AddSqlClientInstrumentationWithEnrichment(
            this TracerProviderBuilder builder,
            bool recordException = false,
            bool enableTextEnrichment = false)
        {
#if NETFRAMEWORK
            return builder.AddSqlClientInstrumentation();
#else
            return builder.AddSqlClientInstrumentation(opt =>
            {
                opt.RecordException = recordException;
                if (enableTextEnrichment)
                    opt.EnrichWithSqlCommand = EnrichWithSqlCommand;
            });
#endif
        }

#if !NETFRAMEWORK
        private static void EnrichWithSqlCommand(Activity? activity, object rawObject) =>
            DbCommandEnricher.Enrich(activity, rawObject as DbCommand, "SqlClientInstrumentation");
#endif
    }
}
