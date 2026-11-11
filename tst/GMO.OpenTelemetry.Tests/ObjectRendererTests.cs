using System.Diagnostics;
using System.Text.Json;
using Moq;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    [Collection("OpenTelemetryEnricherState")]
    public class ObjectRendererTests
    {
        internal static JsonElement Json(object? rendered) => JsonDocument.Parse(Assert.IsType<string>(rendered)).RootElement;

        internal static Level Nested() => new Level
        {
            Name = "L1",
            Child = new Level { Name = "L2", Child = new Level { Name = "L3", Child = new Level { Name = "L4" } } },
        };

        [Fact]
        public void DepthThree_StopsAtTheThirdLevel()
        {
            var json = Json(ObjectRenderer.Render(Nested(), 3));

            Assert.Equal("L1", json.GetProperty("Name").GetString());
            var level2 = json.GetProperty("Child");
            Assert.Equal("L2", level2.GetProperty("Name").GetString());
            Assert.Equal(JsonValueKind.Null, level2.GetProperty("Child").GetProperty("Name").ValueKind);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void ZeroOrNegativeDepth_UsesTheDefault(int depth)
        {
            Assert.Equal(ObjectRenderer.Render(Nested(), ObjectRenderer.DefaultMaxDepth), ObjectRenderer.Render(Nested(), depth));
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void Depth_MeansTheSameAsSerilogDestructuring(int depth)
        {
            var sink = new CaptureSink();
            using (var logger = new LoggerConfiguration().Destructure.ToMaximumDepth(depth).WriteTo.Sink(sink).CreateLogger())
            {
                logger.Information("{@Value}", Nested());
            }

            Assert.Equal(DeepestName(sink.Events.Single().Properties["Value"]), DeepestName(Json(ObjectRenderer.Render(Nested(), depth))));
        }

        [Fact]
        public void CyclicGraph_RendersWithoutThrowing()
        {
            var node = new Level { Name = "root" };
            node.Child = node;

            var json = Json(ObjectRenderer.Render(node, 3));

            Assert.Equal("root", json.GetProperty("Name").GetString());
            Assert.Equal(JsonValueKind.Null, json.GetProperty("Child").ValueKind);
        }

        [Fact]
        public void EntityFrameworkProxy_IsNeverWalked()
        {
            var proxy = new System.Data.Entity.DynamicProxies.Employee_Proxy();

            var rendered = ObjectRenderer.Render(proxy, 3);

            Assert.Equal(typeof(System.Data.Entity.DynamicProxies.Employee_Proxy).FullName, Json(rendered).GetString());
            Assert.Equal(0, proxy.Reads());
        }

        [Fact]
        public void ObjectHoldingEntities_SkipsThoseMembersWithoutCallingThem()
        {
            var holder = new EntityHolder();

            var json = Json(ObjectRenderer.Render(holder, 3));

            Assert.Equal("x", json.GetProperty("Label").GetString());
            Assert.False(json.TryGetProperty("Entity", out _));
            Assert.False(json.TryGetProperty("Entities", out _));
            Assert.Equal(0, holder.Reads());
        }

        [Fact]
        public void ThrowingGetter_DropsThatPropertyOnly()
        {
            var json = Json(ObjectRenderer.Render(new HalfBroken(), 3));

            Assert.Equal("ok", json.GetProperty("Fine").GetString());
            Assert.False(json.TryGetProperty("Broken", out _));
        }

        [Fact]
        public void JsonIgnore_IsHonouredWithoutRunningTheGetter()
        {
            var value = new WithSecrets();
            var rendered = Assert.IsType<string>(ObjectRenderer.Render(value, 3));

            Assert.Equal("visible", Json(rendered).GetProperty("Name").GetString());
            Assert.DoesNotContain("hunter2", rendered);
            Assert.DoesNotContain("secret-api-key", rendered);
            Assert.DoesNotContain("legacy-token", rendered);
            Assert.Equal(0, value.IgnoredReads());
        }

#if !NETFRAMEWORK
        [Fact]
        public void JsonIgnore_ConditionalOmitsOnlyNullOrDefault()
        {
            var unset = Json(ObjectRenderer.Render(new Conditional(), 3));
            Assert.False(unset.TryGetProperty("Note", out _));
            Assert.False(unset.TryGetProperty("Retries", out _));
            Assert.False(unset.TryGetProperty("Label", out _));
            Assert.Equal(JsonValueKind.Null, unset.GetProperty("Kept").ValueKind);

            var set = Json(ObjectRenderer.Render(new Conditional { Note = "n", Retries = 2, Label = "l" }, 3));
            Assert.Equal("n", set.GetProperty("Note").GetString());
            Assert.Equal(2, set.GetProperty("Retries").GetInt32());
            Assert.Equal("l", set.GetProperty("Label").GetString());
        }

        // At the last level every member would be written as null, which a null or default condition omits.
        [Fact]
        public void JsonIgnore_ConditionalAtTheLastLevel_OmitsTheMember()
        {
            var inner = Json(ObjectRenderer.Render(new { Inner = new Conditional { Note = "n", Retries = 2, Label = "l" } }, 2))
                .GetProperty("Inner");

            Assert.Equal(new[] { "Kept" }, inner.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal(JsonValueKind.Null, inner.GetProperty("Kept").ValueKind);
        }
#endif

        [Fact]
        public void JsonIgnore_AppliesToNestedObjects()
        {
            var rendered = Assert.IsType<string>(ObjectRenderer.Render(new { Account = new WithSecrets() }, 3));

            Assert.Equal("visible", Json(rendered).GetProperty("Account").GetProperty("Name").GetString());
            Assert.DoesNotContain("hunter2", rendered);
        }

        [Fact]
        public void ContextsSetsAndQueries_AreNeverEnumerated()
        {
            var service = new ServiceHoldingData();

            var json = Json(ObjectRenderer.Render(service, 3));

            Assert.Equal("svc", json.GetProperty("Name").GetString());
            Assert.False(json.TryGetProperty("Orders", out _));
            Assert.False(json.TryGetProperty("Db", out _));
            Assert.Equal(typeof(CountingQuery<int>).FullName, json.GetProperty("Pending").GetString());
            Assert.Equal(0, service.Orders.Enumerations);
            Assert.Equal(0, service.Db.Orders.Enumerations);
            Assert.Equal(0, service.Pending.Enumerations);
        }

        [Fact]
        public void EntityFrameworkContexts_AreNeverWalked()
        {
            var core = new OrdersCoreContext();
            var ef6 = new OrdersContext();

            Assert.Equal("\"" + typeof(OrdersCoreContext).FullName + "\"", ObjectRenderer.Render(core, 3));
            Assert.Equal("\"" + typeof(OrdersContext).FullName + "\"", ObjectRenderer.Render(ef6, 3));
            Assert.Equal(0, core.Reads());
            Assert.Equal(0, ef6.Orders.Enumerations);
        }

        [Fact]
        public void QueryAtTheRoot_IsNotEnumerated()
        {
            var query = new CountingQuery<int>();

            ObjectRenderer.Render(query, 3);

            Assert.Equal(0, query.Enumerations);
        }

        [Fact]
        public void LastLevel_WritesMembersAsNullWithoutRunningTheirGetters()
        {
            var leaf = new CountingLeaf();

            var json = Json(ObjectRenderer.Render(new { Child = new { Leaf = leaf } }, 3));

            var rendered = json.GetProperty("Child").GetProperty("Leaf");
            Assert.Equal(JsonValueKind.Null, rendered.GetProperty("Value").ValueKind);
            Assert.Equal(0, leaf.Reads);
        }

        [Fact]
        public void JsonPropertyName_RenamesTheMember()
        {
            var json = Json(ObjectRenderer.Render(new Renamed { UserId = 5 }, 3));

            Assert.Equal(5, json.GetProperty("user_id").GetInt32());
            Assert.False(json.TryGetProperty("UserId", out _));
        }

        [Fact]
        public void ParsedJson_RendersAsItself()
        {
            using var document = JsonDocument.Parse("{\"a\":1,\"b\":[true]}");

            Assert.Equal("{\"a\":1,\"b\":[true]}", ObjectRenderer.Render(document.RootElement, 3));
            Assert.Equal("{\"a\":1,\"b\":[true]}", ObjectRenderer.Render(document, 3));
            Assert.Equal("{\"Payload\":{\"a\":1,\"b\":[true]}}", ObjectRenderer.Render(new { Payload = document.RootElement }, 3));
        }

#if NET
        [Fact]
        public void JsonNode_RendersAsItself()
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse("{\"a\":1}");

            Assert.Equal("{\"Payload\":{\"a\":1}}", ObjectRenderer.Render(new { Payload = node }, 3));
        }

        [Fact]
        public void DateAndTimeOnly_RenderAsIsoText()
        {
            var json = Json(ObjectRenderer.Render(new { Date = new DateOnly(2026, 10, 19), Time = new TimeOnly(9, 30, 5) }, 3));

            Assert.Equal("2026-10-19", json.GetProperty("Date").GetString());
            Assert.StartsWith("09:30:05", json.GetProperty("Time").GetString());
            Assert.Equal("\"2026-10-19\"", ObjectRenderer.Render(new DateOnly(2026, 10, 19), 3));
        }

#endif

#if NET7_0_OR_GREATER
        [Fact]
        public void WideNumbers_RenderAsInvariantText()
        {
            var json = Json(ObjectRenderer.Render(new { Big = (Int128)long.MaxValue * 4, Small = (Half)1.5 }, 3));

            Assert.Equal("36893488147419103228", json.GetProperty("Big").GetString());
            Assert.Equal("1.5", json.GetProperty("Small").GetString());
        }
#endif

        [Fact]
        public void LargeCollection_IsCapped()
        {
            var json = Json(ObjectRenderer.Render(Enumerable.Range(0, 1000).ToList(), 3));

            Assert.Equal(ObjectRenderer.MaxCollectionItems, json.GetArrayLength());
        }

        [Fact]
        public void LargeDictionary_IsCapped()
        {
            var json = Json(ObjectRenderer.Render(Enumerable.Range(0, 1000).ToDictionary(i => i, i => i), 3));

            Assert.Equal(ObjectRenderer.MaxCollectionItems, json.EnumerateObject().Count());
        }

        [Fact]
        public void DictionaryKeyWithoutText_RendersAsEmptyName()
        {
            var json = Json(ObjectRenderer.Render(new System.Collections.Hashtable { [new NoText()] = 1 }, 3));

            Assert.Equal(1, json.GetProperty("").GetInt32());
        }

        [Fact]
        public void BigInteger_RendersAsInvariantText()
        {
            var json = Json(ObjectRenderer.Render(new { Big = System.Numerics.BigInteger.Pow(10, 30) }, 3));

            Assert.Equal("1000000000000000000000000000000", json.GetProperty("Big").GetString());
        }

        // System.Text.Json refuses NaN and infinities, so the value falls back to its own text rather
        // than failing the span.
        [Fact]
        public void UnserializableValue_FallsBackToToString()
        {
            var value = new { Ratio = double.NaN };

            Assert.Equal(value.ToString(), ObjectRenderer.Render(value, 3));
        }

        [Fact]
        public void ReflectionObjects_RenderAsText()
        {
            var json = Json(ObjectRenderer.Render(new { Kind = typeof(Level) }, 3));

            Assert.Equal(typeof(Level).ToString(), json.GetProperty("Kind").GetString());
        }

        [Fact]
        public void SetTag_UsesTheConfiguredDepth()
        {
            using var source = new ActivitySource("GMO.Tests.Renderer." + Guid.NewGuid().ToString("N"));
            using var listener = new ActivityListener
            {
                ShouldListenTo = s => s.Name == source.Name,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            };
            ActivitySource.AddActivityListener(listener);

            using var wrapper = new ActivityWrapper("depth", source);
            wrapper.SetTag("value", Nested());

            Assert.Equal("L2", DeepestName(Json(wrapper.Activity!.GetTagItem("value"))));
        }

        internal static string? DeepestName(LogEventPropertyValue? value)
        {
            string? deepest = null;
            while (value is StructureValue structure)
            {
                var props = structure.Properties.ToDictionary(p => p.Name, p => p.Value);
                if (props.TryGetValue("Name", out var name) && name is ScalarValue { Value: string s }) deepest = s;
                value = props.TryGetValue("Child", out var child) ? child : null;
            }
            return deepest;
        }

        internal static string? DeepestName(JsonElement json)
        {
            string? deepest = null;
            while (json.ValueKind == JsonValueKind.Object)
            {
                if (json.TryGetProperty("Name", out var name) && name.ValueKind == JsonValueKind.String) deepest = name.GetString();
                if (!json.TryGetProperty("Child", out json)) break;
            }
            return deepest;
        }

        public sealed class Level
        {
            public string? Name { get; set; }
            public Level? Child { get; set; }
        }

        public sealed class NoText
        {
            public override string? ToString() => null;
        }

        public sealed class HalfBroken
        {
            public string Fine { get; } = "ok";
            public string Broken => throw new InvalidOperationException("getter invoked after " + Fine);
        }

        // Counts reads rather than throwing: the renderer drops a property whose getter throws, so a
        // throwing getter would pass whether or not the member was skipped.
        public sealed class EntityHolder
        {
            private int _reads;

            public int Reads() => _reads;

            public string Label { get; } = "x";

            public System.Data.Entity.DynamicProxies.Employee_Proxy Entity
            {
                get { _reads++; return new System.Data.Entity.DynamicProxies.Employee_Proxy(); }
            }

            public List<System.Data.Entity.DynamicProxies.Employee_Proxy> Entities
            {
                get { _reads++; return new List<System.Data.Entity.DynamicProxies.Employee_Proxy>(); }
            }
        }

        // Plain values, not throwing getters: the renderer already drops a property whose getter throws,
        // so a throwing getter would pass whether or not the attribute was honoured.
        public sealed class WithSecrets
        {
            private int _reads;

            public int IgnoredReads() => _reads;

            public string Name { get; } = "visible";

            [System.Text.Json.Serialization.JsonIgnore]
            public string Password
            {
                get { _reads++; return "hunter2"; }
            }

#if !NETFRAMEWORK
            [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Always)]
            public string ApiKey
            {
                get { _reads++; return "secret-api-key"; }
            }
#endif

            [Newtonsoft.Json.JsonIgnore]
            public string LegacyToken
            {
                get { _reads++; return "legacy-token"; }
            }
        }

#if !NETFRAMEWORK
        // net48 resolves System.Text.Json 4.7.2, whose attribute has no Condition.
        public sealed class Conditional
        {
            [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
            public string? Note { get; set; }

            [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
            public int Retries { get; set; }

            [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
            public string? Label { get; set; }

            public string? Kept { get; set; }
        }
#endif

        public sealed class CountingLeaf
        {
            private int _reads;

            public int Reads => _reads;

            public string Value
            {
                get { _reads++; return "read"; }
            }
        }

        public sealed class Renamed
        {
            [System.Text.Json.Serialization.JsonPropertyName("user_id")]
            public int UserId { get; set; }
        }

        internal sealed class CaptureSink : ILogEventSink
        {
            public List<LogEvent> Events { get; } = new List<LogEvent>();
            public void Emit(LogEvent logEvent) => Events.Add(logEvent);
        }
    }

    [CollectionDefinition(nameof(RendererDepthState), DisableParallelization = true)]
    public class RendererDepthState { }

    // The depth is process-wide: changing it while other classes render in parallel corrupts their
    // output, so the tests that change it run alone.
    [Collection(nameof(RendererDepthState))]
    public class ObjectRendererConfigurationTests
    {
        [Fact]
        public void Configure_TakesTheValueLengthFromTheOptions()
        {
            try
            {
                ObjectRenderer.Configure(new TestOptions { MaxAttributeValueLength = 10 });
                Assert.Equal(10, ObjectRenderer.MaxValueLength);
                Assert.Equal("0123456789", ObjectRenderer.Shorten("0123456789abc"));
                Assert.Equal(42, ObjectRenderer.Shorten((object)42));
            }
            finally
            {
                ObjectRenderer.Configure(new TestOptions());
            }
        }

        // New Relic truncates span values past 4,095 characters and reports each one; the span processor
        // shortens tags but cannot reach events, so the wrapper shortens what it puts there itself.
        [Fact]
        public void ActivityWrapper_ShortensEventValuesAndStatusDescriptions()
        {
            using var source = new ActivitySource("Test.Shorten");
            using var listener = new ActivityListener
            {
                ShouldListenTo = s => s.Name == "Test.Shorten",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            };
            ActivitySource.AddActivityListener(listener);
            try
            {
                ObjectRenderer.Configure(new TestOptions { MaxAttributeValueLength = 10 });
                using var wrapper = new ActivityWrapper("op", source);
                var longText = new string('x', 100);

                wrapper.AddEvent("custom", new Dictionary<string, object> { ["value"] = longText, ["number"] = 5 });
                wrapper.RecordException(new InvalidOperationException(longText));

                var activity = wrapper.Activity!;
                Assert.Equal(10, ((string)activity.Events.Single(e => e.Name == "custom").Tags.Single(t => t.Key == "value").Value!).Length);
                Assert.Equal(5, activity.Events.Single(e => e.Name == "custom").Tags.Single(t => t.Key == "number").Value);
                Assert.Equal(10, ((string)activity.Events.Single(e => e.Name == "exception").Tags.Single(t => t.Key == "exception.message").Value!).Length);
                Assert.Equal(10, activity.StatusDescription!.Length);
            }
            finally
            {
                ObjectRenderer.Configure(new TestOptions());
            }
        }

        [Fact]
        public void Configure_InterfaceOnlyOptions_TakeTheValueLengthButNotTheDepth()
        {
            try
            {
                ObjectRenderer.Configure(new TestOptions { MaxSerializationDepth = 2 });
                var options = new Mock<IOpenTelemetryOptions>();
                options.SetupGet(o => o.MaxAttributeValueLength).Returns(12);

                ObjectRenderer.Configure(options.Object);

                Assert.Equal(12, ObjectRenderer.MaxValueLength);
                Assert.Equal(2, ObjectRenderer.MaxDepth);
            }
            finally
            {
                ObjectRenderer.Configure(new TestOptions());
            }
        }

        [Fact]
        public void Configure_NoUsableValueLength_KeepsTheCurrentOne()
        {
            try
            {
                ObjectRenderer.Configure(new TestOptions { MaxAttributeValueLength = 10 });

                ObjectRenderer.Configure(new TestOptions { MaxAttributeValueLength = 0 });
                Assert.Equal(10, ObjectRenderer.MaxValueLength);

                ObjectRenderer.Configure(null!);
                Assert.Equal(10, ObjectRenderer.MaxValueLength);
            }
            finally
            {
                ObjectRenderer.Configure(new TestOptions());
            }
        }

        [Fact]
        public void Configure_TakesTheDepthFromTheOptions()
        {
            try
            {
                ObjectRenderer.Configure(new TestOptions { MaxSerializationDepth = 2 });
                Assert.Equal(2, ObjectRenderer.MaxDepth);

                ObjectRenderer.Configure(new TestOptions { MaxSerializationDepth = 0 });
                Assert.Equal(ObjectRenderer.DefaultMaxDepth, ObjectRenderer.MaxDepth);
            }
            finally
            {
                ObjectRenderer.Configure(new TestOptions());
            }
        }

#if !NETFRAMEWORK
        [Theory]
        [InlineData(2, "L1")]
        [InlineData(3, "L2")]
        public void SerilogPipeline_LimitsDestructuringToTheConfiguredDepth(int depth, string deepestName)
        {
            try
            {
                var options = new TestOptions
                {
                    Enabled = true,
                    EnableLogging = true,
                    LoggingEndpoint = "http://localhost:4318",
                    Otlp = new OtlpSettings { Protocol = global::OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf },
                    MaxSerializationDepth = depth,
                };
                var capture = new ObjectRendererTests.CaptureSink();
                var logConfig = new LoggerConfiguration().WriteTo.Sink(capture);
                Serilog.OpenTelemetryExtensions.AddOpenTelemetryWithEnricher(logConfig, options,
                    new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), new Mock<IAttributeEnricher>().Object);

                using (var logger = logConfig.CreateLogger())
                {
                    logger.Information("{@Value}", ObjectRendererTests.Nested());
                }

                Assert.Equal(deepestName, ObjectRendererTests.DeepestName(capture.Events.Single().Properties["Value"]));
            }
            finally
            {
                ObjectRenderer.Configure(new TestOptions());
            }
        }
#endif
    }
}

namespace System.Data.Entity.DynamicProxies
{
    // The namespace EF6 gives its lazy-loading proxies; the renderer recognises entities by it.
    public sealed class Employee_Proxy
    {
        private int _reads;

        public int Reads() => _reads;

        public string Name
        {
            get { _reads++; return "employee"; }
        }
    }
}

#if !NET6_0
// Stand-in for Newtonsoft's attribute, which the renderer matches by full name. The net6 suite gets the
// real one through Microsoft.TestPlatform.TestHost, and a second definition there is CS0436.
namespace Newtonsoft.Json
{
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class JsonIgnoreAttribute : Attribute { }
}
#endif
