using System.Data;
using System.Diagnostics;
using System.Reflection;
using GMO.OpenTelemetry.SqlClient;
using OpenTelemetry;
using Xunit;

// EnrichWithSqlCommand only exists in the !NETFRAMEWORK build of SqlClientInstrumentationExtensions
#if !NETFRAMEWORK
namespace GMO.OpenTelemetry.Tests
{
    public class SqlClientInstrumentationExtensionsTests : IDisposable
    {
        private readonly ActivitySource _source = new("Test.SqlClient");
        private readonly ActivityListener _listener;

        public SqlClientInstrumentationExtensionsTests()
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

        private static void Enrich(Activity? activity, object rawObject) =>
            typeof(SqlClientInstrumentationExtensions)
                .GetMethod("EnrichWithSqlCommand", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [activity, rawObject]);

        [Fact]
        public void AddSqlClientInstrumentationWithEnrichment_NoEnrichment_DoesNotThrow()
        {
            var builder = Sdk.CreateTracerProviderBuilder();
            var result = builder.AddSqlClientInstrumentationWithEnrichment(recordException: false, enableTextEnrichment: false);
            Assert.NotNull(result);
        }

        [Fact]
        public void AddSqlClientInstrumentationWithEnrichment_WithEnrichment_DoesNotThrow()
        {
            var builder = Sdk.CreateTracerProviderBuilder();
            var result = builder.AddSqlClientInstrumentationWithEnrichment(recordException: true, enableTextEnrichment: true);
            Assert.NotNull(result);
        }

        [Fact]
        public void EnrichWithSqlCommand_NullActivity_DoesNotThrow()
        {
            var exception = Record.Exception(() => Enrich(null, new TestDbCommand()));
            Assert.Null(exception);
        }

        [Fact]
        public void EnrichWithSqlCommand_NotDbCommand_SetsNoTags()
        {
            using var activity = _source.StartActivity("Test")!;
            var exception = Record.Exception(() => Enrich(activity, "not a command"));
            Assert.Null(exception);
            Assert.Empty(activity.TagObjects);
        }

        [Fact]
        public void EnrichWithSqlCommand_DbCommand_SetsSharedEnrichmentTags()
        {
            using var activity = _source.StartActivity("Test")!;
            var cmd = new TestDbCommand { CommandTimeout = 60, CommandType = CommandType.StoredProcedure };
            cmd.TestParams.Add(new TestDbParameter { ParameterName = "@id", Value = "abc" });
            Enrich(activity, cmd);

            Assert.Equal(60, activity.GetTagItem("db.commandTimeout"));
            Assert.Equal("StoredProcedure", activity.GetTagItem("db.commandType"));
            Assert.Equal("{\"@id\":\"abc\"}", activity.GetTagItem("db.params"));
        }
    }
}
#endif
