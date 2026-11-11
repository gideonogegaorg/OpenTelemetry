using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class CoreNewRelicLogLimitsTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("plain text")]
        [InlineData("[1, 2]")]
        [InlineData("{ not json }")]
        [InlineData("{\"a\": 1} trailing")]
        public void NotAJsonObject_StaysOneAttribute(string? text)
        {
            Assert.Null(NewRelicLogLimits.JsonObjectFields(text));
        }

        // Nested objects flatten to one field per leaf, arrays stay one field and nulls are not stored.
        [Fact]
        public void JsonObject_CountsTheFieldsNewRelicStores()
        {
            Assert.Equal(3, NewRelicLogLimits.JsonObjectFields("  {\"a\": 1, \"b\": {\"c\": \"x\", \"d\": null}, \"e\": [1, 2, 3]}"));
        }

        [Fact]
        public void EmptyJsonObject_HasNoFields()
        {
            Assert.Equal(0, NewRelicLogLimits.JsonObjectFields("{}"));
        }
    }
}
