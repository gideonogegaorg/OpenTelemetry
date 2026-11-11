#if !NETFRAMEWORK
using System.Data;
using System.Data.Common;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace GMO.OpenTelemetry
{
    internal static class DbCommandEnricher
    {
        internal static void Enrich(Activity? activity, DbCommand? command, string component)
        {
            if (activity == null || command == null)
                return;

            try
            {
                activity.SetTag("db.commandTimeout", command.CommandTimeout);
                activity.SetTag("db.commandType", command.CommandType.ToString());
                SetIsolationLevel(activity, command.Transaction);

                var prms = BuildParameters(command.Parameters, out var hasValue);
                if (hasValue)
                    SetParams(activity, prms);
            }
            catch (Exception ex)
            {
                GmoTelemetryEventSource.Log.OperationFailed(component, "enriching command", GmoTelemetryEventSource.Describe(ex));
            }
        }

        internal static void Enrich(Activity? activity, DbBatch? batch, string component)
        {
            if (activity == null || batch == null)
                return;

            try
            {
                activity.SetTag("db.commandTimeout", batch.Timeout);
                activity.SetTag("db.operation.batch.size", batch.BatchCommands.Count);
                SetIsolationLevel(activity, batch.Transaction);

                var perCommand = new List<object>();
                var any = false;
                foreach (DbBatchCommand command in batch.BatchCommands)
                {
                    perCommand.Add(BuildParameters(command.Parameters, out var hasValue));
                    any |= hasValue;
                }

                if (any)
                    SetParams(activity, perCommand);
            }
            catch (Exception ex)
            {
                GmoTelemetryEventSource.Log.OperationFailed(component, "enriching batch", GmoTelemetryEventSource.Describe(ex));
            }
        }

        private static void SetIsolationLevel(Activity activity, DbTransaction? transaction)
        {
            if (transaction != null)
                activity.SetTag("db.transaction.isolation_level", transaction.IsolationLevel.ToString());
        }

        private static void SetParams(Activity activity, object value)
        {
            try
            {
                activity.SetTag("db.params", JsonSerializer.Serialize(value));
            }
            catch
            {
                activity.SetTag("db.params", "[serialization failed]");
            }
        }

        // Unnamed parameters are Npgsql positional ($1, $2, ...): all unnamed -> array in order (null kept to
        // preserve position); mixed -> unnamed keyed "$N" by 1-based position so none overwrite each other.
        private static object BuildParameters(DbParameterCollection parameters, out bool hasValue)
        {
            var all = new List<DbParameter>(parameters.Count);
            var anyNamed = false;
            var anyUnnamed = false;
            foreach (DbParameter p in parameters)
            {
                all.Add(p);
                if (string.IsNullOrEmpty(p.ParameterName))
                    anyUnnamed = true;
                else
                    anyNamed = true;
            }

            return anyUnnamed && !anyNamed
                ? BuildPositionalParameters(all, out hasValue)
                : BuildNamedParameters(all, out hasValue);
        }

        private static List<object?> BuildPositionalParameters(List<DbParameter> all, out bool hasValue)
        {
            hasValue = false;
            var values = new List<object?>(all.Count);
            foreach (var p in all)
            {
                var converted = TryConvert(p.Value, out var value);
                hasValue |= converted;
                values.Add(converted ? value : null);
            }

            return values;
        }

        private static Dictionary<string, object> BuildNamedParameters(List<DbParameter> all, out bool hasValue)
        {
            hasValue = false;
            var prms = new Dictionary<string, object>();
            for (var i = 0; i < all.Count; i++)
            {
                if (!TryConvert(all[i].Value, out var value))
                    continue;

                var name = all[i].ParameterName;
                prms[string.IsNullOrEmpty(name) ? "$" + (i + 1).ToString(CultureInfo.InvariantCulture) : name] = value;
                hasValue = true;
            }

            return prms;
        }

        private static bool TryConvert(object? raw, [NotNullWhen(true)] out object? value)
        {
            value = null;
            if (raw == null || raw is DBNull)
                return false;

            if (raw is DataTable dt)
                value = ExtractDataTableIds(dt);
            else if (raw is INullable sqlNullable)
            {
                if (sqlNullable.IsNull)
                    return false;
                value = sqlNullable.ToString()!;
            }
            else
                value = raw;

            return true;
        }

        private static List<string> ExtractDataTableIds(DataTable dt)
        {
            var ids = new List<string>();
            if (!dt.Columns.Contains("Id"))
                return ids;

            foreach (DataRow row in dt.Rows)
            {
                if (row["Id"] != DBNull.Value)
                    ids.Add(row["Id"]?.ToString() ?? string.Empty);
            }

            return ids;
        }
    }
}
#endif
