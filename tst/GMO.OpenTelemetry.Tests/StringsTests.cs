using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class StringsTests
    {
        [Theory]
        [InlineData(null, true)]
        [InlineData("", true)]
        [InlineData(" ", false)]
        [InlineData("a", false)]
        public void IsNullOrEmpty_MatchesString(string? value, bool expected) =>
            Assert.Equal(expected, Strings.IsNullOrEmpty(value));

        [Theory]
        [InlineData(null, true)]
        [InlineData("", true)]
        [InlineData(" 	", true)]
        [InlineData("a", false)]
        public void IsNullOrWhiteSpace_MatchesString(string? value, bool expected) =>
            Assert.Equal(expected, Strings.IsNullOrWhiteSpace(value));
    }
}
