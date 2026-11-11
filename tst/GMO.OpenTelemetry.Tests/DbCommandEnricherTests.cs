#if !NETFRAMEWORK
using System.Collections;
using System.Data;
using System.Data.Common;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Moq;
using Npgsql;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    internal sealed class TestDbParameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
        public override bool IsNullable { get; set; }
        [AllowNull]
        public override string ParameterName { get; set; } = string.Empty;
        public override int Size { get; set; }
        [AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;
        public override bool SourceColumnNullMapping { get; set; }
        public override object? Value { get; set; }
        public override void ResetDbType() { }
    }

    internal sealed class TestDbParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _items = new();

        public void Add(DbParameter p) => _items.Add(p);

        public override int Count => _items.Count;
        public override object SyncRoot => _items;
        public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }
        public override void AddRange(Array values) { }
        public override void Clear() => _items.Clear();
        public override bool Contains(object value) => _items.Contains((DbParameter)value);
        public override bool Contains(string value) => _items.Any(p => p.ParameterName == value);
        public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
        public override IEnumerator GetEnumerator() => _items.GetEnumerator();
        public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => _items.FindIndex(p => p.ParameterName == parameterName);
        public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
        public override void Remove(object value) => _items.Remove((DbParameter)value);
        public override void RemoveAt(int index) => _items.RemoveAt(index);
        public override void RemoveAt(string parameterName) => _items.RemoveAll(p => p.ParameterName == parameterName);
        protected override DbParameter GetParameter(int index) => _items[index];
        protected override DbParameter GetParameter(string parameterName) => _items.First(p => p.ParameterName == parameterName);
        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
        protected override void SetParameter(string parameterName, DbParameter? value) { }
    }

    internal sealed class TestDbCommand : DbCommand
    {
        private readonly TestDbParameterCollection _params = new();
        private DbTransaction? _transaction;

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; } = 30;
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection => _params;
        protected override DbTransaction? DbTransaction { get => _transaction; set => _transaction = value; }

        public TestDbParameterCollection TestParams => _params;
        public void SetTransaction(DbTransaction? t) => _transaction = t;

        public override void Cancel() { }
        public override int ExecuteNonQuery() => 0;
        public override object? ExecuteScalar() => null;
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new TestDbParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotImplementedException();
    }

    internal sealed class SelfReferencing
    {
        public SelfReferencing Self => this;
    }

    public class DbCommandEnricherTests : IDisposable
    {
        private readonly ActivitySource _source = new("Test.DbCommandEnricher");
        private readonly ActivityListener _listener;

        public DbCommandEnricherTests()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = s => s.Name == _source.Name,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose()
        {
            _listener.Dispose();
            _source.Dispose();
            GC.SuppressFinalize(this);
        }

        private static void Enrich(Activity? activity, DbCommand? command) =>
            DbCommandEnricher.Enrich(activity, command, "Test");

        [Fact]
        public void Enrich_NullActivity_DoesNotThrow()
        {
            var exception = Record.Exception(() => Enrich(null, new TestDbCommand()));
            Assert.Null(exception);
        }

        [Fact]
        public void Enrich_NullCommand_SetsNoTags()
        {
            using var activity = _source.StartActivity("Test")!;
            Enrich(activity, null);
            Assert.Empty(activity.TagObjects);
        }

        [Fact]
        public void Enrich_SetsCommandTags()
        {
            using var activity = _source.StartActivity("Test")!;
            Enrich(activity, new TestDbCommand { CommandTimeout = 60, CommandType = CommandType.StoredProcedure });

            Assert.Equal(60, activity.GetTagItem("db.commandTimeout"));
            Assert.Equal("StoredProcedure", activity.GetTagItem("db.commandType"));
        }

        [Fact]
        public void Enrich_WithTransaction_SetsIsolationLevel()
        {
            using var activity = _source.StartActivity("Test")!;
            var txn = new Mock<DbTransaction>();
            txn.Setup(t => t.IsolationLevel).Returns(IsolationLevel.ReadCommitted);
            var cmd = new TestDbCommand();
            cmd.SetTransaction(txn.Object);
            Enrich(activity, cmd);

            Assert.Equal("ReadCommitted", activity.GetTagItem("db.transaction.isolation_level"));
        }

        [Fact]
        public void Enrich_NoTransaction_NoIsolationLevelTag()
        {
            using var activity = _source.StartActivity("Test")!;
            Enrich(activity, new TestDbCommand());
            Assert.Null(activity.GetTagItem("db.transaction.isolation_level"));
        }

        [Fact]
        public void Enrich_NoParameters_NoDbParamsTag()
        {
            using var activity = _source.StartActivity("Test")!;
            Enrich(activity, new TestDbCommand());
            Assert.Null(activity.GetTagItem("db.params"));
        }

        [Fact]
        public void Enrich_WithStringParameter_SetsDbParamsTag()
        {
            using var activity = _source.StartActivity("Test")!;
            var cmd = new TestDbCommand();
            cmd.TestParams.Add(new TestDbParameter { ParameterName = "@id", Value = "abc" });
            Enrich(activity, cmd);

            Assert.Equal("{\"@id\":\"abc\"}", activity.GetTagItem("db.params"));
        }

        [Fact]
        public void Enrich_NullParameterValue_Skipped()
        {
            using var activity = _source.StartActivity("Test")!;
            var cmd = new TestDbCommand();
            cmd.TestParams.Add(new TestDbParameter { ParameterName = "@id", Value = null });
            Enrich(activity, cmd);
            Assert.Null(activity.GetTagItem("db.params"));
        }

        [Fact]
        public void Enrich_DBNullParameterValue_Skipped()
        {
            using var activity = _source.StartActivity("Test")!;
            var cmd = new TestDbCommand();
            cmd.TestParams.Add(new TestDbParameter { ParameterName = "@id", Value = DBNull.Value });
            Enrich(activity, cmd);
            Assert.Null(activity.GetTagItem("db.params"));
        }

        [Fact]
        public void Enrich_SqlNullableIsNull_Skipped()
        {
            using var activity = _source.StartActivity("Test")!;
            var cmd = new TestDbCommand();
            cmd.TestParams.Add(new TestDbParameter { ParameterName = "@val", Value = SqlInt32.Null });
            Enrich(activity, cmd);
            Assert.Null(activity.GetTagItem("db.params"));
        }

        [Fact]
        public void Enrich_SqlNullableNotNull_IncludesParam()
        {
            using var activity = _source.StartActivity("Test")!;
            var cmd = new TestDbCommand();
            cmd.TestParams.Add(new TestDbParameter { ParameterName = "@val", Value = new SqlInt32(42) });
            Enrich(activity, cmd);

            Assert.Equal("{\"@val\":\"42\"}", activity.GetTagItem("db.params"));
        }

        [Fact]
        public void Enrich_DataTableNoIdColumn_SetsEmptyList()
        {
            using var activity = _source.StartActivity("Test")!;
            var dt = new DataTable();
            dt.Columns.Add("Name");
            dt.Rows.Add("val1");
            var cmd = new TestDbCommand();
            cmd.TestParams.Add(new TestDbParameter { ParameterName = "@tvp", Value = dt });
            Enrich(activity, cmd);

            Assert.Equal("{\"@tvp\":[]}", activity.GetTagItem("db.params"));
        }

        [Fact]
        public void Enrich_DataTableParameter_ExtractsIds()
        {
            using var activity = _source.StartActivity("Test")!;
            var dt = new DataTable();
            dt.Columns.Add("Id");
            dt.Rows.Add("row1");
            dt.Rows.Add("row2");
            var cmd = new TestDbCommand();
            cmd.TestParams.Add(new TestDbParameter { ParameterName = "@tvp", Value = dt });
            Enrich(activity, cmd);

            Assert.Equal("{\"@tvp\":[\"row1\",\"row2\"]}", activity.GetTagItem("db.params"));
        }

        [Fact]
        public void Enrich_UnserializableParameter_SetsFailureMarker()
        {
            using var activity = _source.StartActivity("Test")!;
            var cmd = new TestDbCommand();
            cmd.TestParams.Add(new TestDbParameter { ParameterName = "@obj", Value = new SelfReferencing() });
            Enrich(activity, cmd);

            Assert.Equal("[serialization failed]", activity.GetTagItem("db.params"));
            Assert.Equal(30, activity.GetTagItem("db.commandTimeout"));
        }

        private static TestDbCommand CommandWith(params (string? Name, object? Value)[] parameters)
        {
            var cmd = new TestDbCommand();
            foreach (var (name, value) in parameters)
                cmd.TestParams.Add(new TestDbParameter { ParameterName = name, Value = value });
            return cmd;
        }

        [Fact]
        public void Enrich_AllUnnamedParameters_SetsJsonArrayInOrder()
        {
            using var activity = _source.StartActivity("Test")!;
            Enrich(activity, CommandWith(("", 42), ("", "smoke")));

            Assert.Equal("[42,\"smoke\"]", activity.GetTagItem("db.params"));
        }

        [Fact]
        public void Enrich_AllNamedParameters_SetsJsonObject()
        {
            using var activity = _source.StartActivity("Test")!;
            Enrich(activity, CommandWith(("@id", 42), ("@name", "smoke")));

            Assert.Equal("{\"@id\":42,\"@name\":\"smoke\"}", activity.GetTagItem("db.params"));
        }

        [Fact]
        public void Enrich_MixedParameters_KeysUnnamedByPlaceholderPosition()
        {
            using var activity = _source.StartActivity("Test")!;
            Enrich(activity, CommandWith(("", 1), ("@name", "smoke"), (null, true), ("@skip", null)));

            Assert.Equal("{\"$1\":1,\"@name\":\"smoke\",\"$3\":true}", activity.GetTagItem("db.params"));
        }

        [Fact]
        public void Enrich_UnnamedNullValues_KeepPositionAsJsonNull()
        {
            using var activity = _source.StartActivity("Test")!;
            Enrich(activity, CommandWith(("", null), ("", DBNull.Value), ("", SqlInt32.Null), ("", "x")));

            Assert.Equal("[null,null,null,\"x\"]", activity.GetTagItem("db.params"));
        }

        [Fact]
        public void Enrich_UnnamedParametersAllNull_NoDbParamsTag()
        {
            using var activity = _source.StartActivity("Test")!;
            Enrich(activity, CommandWith(("", null), ("", DBNull.Value)));

            Assert.Null(activity.GetTagItem("db.params"));
        }

        [Fact]
        public void Enrich_UnnamedParameters_KeepValueConversions()
        {
            using var activity = _source.StartActivity("Test")!;
            var dt = new DataTable();
            dt.Columns.Add("Id");
            dt.Rows.Add("row1");
            Enrich(activity, CommandWith(("", new SqlInt32(42)), ("", dt)));

            Assert.Equal("[\"42\",[\"row1\"]]", activity.GetTagItem("db.params"));
        }

        [Fact]
        public void Enrich_UnnamedUnserializableParameter_SetsFailureMarker()
        {
            using var activity = _source.StartActivity("Test")!;
            Enrich(activity, CommandWith(("", 1), ("", new SelfReferencing())));

            Assert.Equal("[serialization failed]", activity.GetTagItem("db.params"));
        }

        [Fact]
        public void Enrich_Batch_EachCommandFollowsParameterShapeRules()
        {
            using var activity = _source.StartActivity("Test")!;
            using var batch = new NpgsqlBatch();
            var named = new NpgsqlBatchCommand("INSERT INTO t (id) VALUES (@id)");
            named.Parameters.AddWithValue("id", 1);
            var positional = new NpgsqlBatchCommand("SELECT $1, $2");
            positional.Parameters.Add(new NpgsqlParameter { Value = 2 });
            positional.Parameters.Add(new NpgsqlParameter { Value = "b" });
            var mixed = new NpgsqlBatchCommand("SELECT 1");
            mixed.Parameters.AddWithValue("a", 3);
            mixed.Parameters.Add(new NpgsqlParameter { Value = "c" });
            batch.BatchCommands.Add(named);
            batch.BatchCommands.Add(positional);
            batch.BatchCommands.Add(mixed);
            batch.BatchCommands.Add(new NpgsqlBatchCommand("SELECT 1"));
            DbCommandEnricher.Enrich(activity, batch, "Test");

            Assert.Equal("[{\"id\":1},[2,\"b\"],{\"a\":3,\"$2\":\"c\"},{}]", activity.GetTagItem("db.params"));
            Assert.Equal(4, activity.GetTagItem("db.operation.batch.size"));
        }

        [Fact]
        public void Enrich_BatchOnlyNullPositionalValues_NoDbParamsTag()
        {
            using var activity = _source.StartActivity("Test")!;
            using var batch = new NpgsqlBatch();
            var positional = new NpgsqlBatchCommand("SELECT $1");
            positional.Parameters.Add(new NpgsqlParameter { Value = DBNull.Value });
            batch.BatchCommands.Add(positional);
            DbCommandEnricher.Enrich(activity, batch, "Test");

            Assert.Null(activity.GetTagItem("db.params"));
        }

        [Fact]
        public void Enrich_NullBatch_SetsNoTags()
        {
            using var activity = _source.StartActivity("Test")!;
            DbCommandEnricher.Enrich(activity, (DbBatch?)null, "Test");
            Assert.Empty(activity.TagObjects);
        }

        [Fact]
        public void Enrich_BatchWithNullActivity_DoesNotReadTheBatch()
        {
            var batch = new Mock<DbBatch>(MockBehavior.Strict);

            DbCommandEnricher.Enrich(null, batch.Object, "Test");

            batch.VerifyNoOtherCalls();
        }

        // Enrichment runs inside the provider's instrumentation callback: a throwing provider property
        // must not escape into the command being traced.
        [Fact]
        public void Enrich_CommandPropertyThrows_ReportsAndDoesNotThrow()
        {
            var component = "comp-" + Guid.NewGuid().ToString("N");
            using var capture = new GmoTelemetryEventSourceTests.Capture();
            using var activity = _source.StartActivity("Test")!;
            var command = new Mock<DbCommand>();
            command.SetupGet(c => c.CommandTimeout).Throws(new InvalidOperationException("connection closed"));

            DbCommandEnricher.Enrich(activity, command.Object, component);

            Assert.Empty(activity.TagObjects);
            var e = Assert.Single(capture.ForComponent(component));
            Assert.Equal("enriching command", e.Payload![1]);
            Assert.Contains("connection closed", (string)e.Payload[2]!);
        }

        [Fact]
        public void Enrich_BatchPropertyThrows_ReportsAndDoesNotThrow()
        {
            var component = "comp-" + Guid.NewGuid().ToString("N");
            using var capture = new GmoTelemetryEventSourceTests.Capture();
            using var activity = _source.StartActivity("Test")!;
            var batch = new Mock<DbBatch>();
            batch.SetupGet(b => b.Timeout).Throws(new InvalidOperationException("batch disposed"));

            DbCommandEnricher.Enrich(activity, batch.Object, component);

            Assert.Empty(activity.TagObjects);
            var e = Assert.Single(capture.ForComponent(component));
            Assert.Equal("enriching batch", e.Payload![1]);
            Assert.Contains("batch disposed", (string)e.Payload[2]!);
        }
    }
}
#endif
