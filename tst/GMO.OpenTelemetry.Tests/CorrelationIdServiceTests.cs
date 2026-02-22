using GMO.OpenTelemetry;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class CorrelationIdServiceTests
    {
        [Fact]
        public void GenerateCorrelationId_ReturnsNonEmptyGuidFormat()
        {
            var service = new CorrelationIdService();
            var id = service.GenerateCorrelationId();

            Assert.False(string.IsNullOrWhiteSpace(id));
            Assert.True(System.Guid.TryParse(id, out _));
            Assert.Equal(36, id.Length); // "D" format: 32 hex + 4 hyphens
        }

        [Fact]
        public void SetCorrelationId_ThenGetCorrelationId_ReturnsSameValue()
        {
            var service = new CorrelationIdService();
            var expected = "test-correlation-id";

            service.SetCorrelationId(expected);
            var actual = service.GetCorrelationId();

            Assert.Equal(expected, actual);
        }

        [Fact]
        public void GetCorrelationId_WhenNothingSet_GeneratesAndReturnsNewId()
        {
            var service = new CorrelationIdService();

            var id = service.GetCorrelationId();

            Assert.False(string.IsNullOrWhiteSpace(id));
            Assert.True(System.Guid.TryParse(id, out _));
        }

        [Fact]
        public void SetCorrelationId_WithNull_GeneratesNewId()
        {
            var service = new CorrelationIdService();

            service.SetCorrelationId(null!);
            var id = service.GetCorrelationId();

            Assert.False(string.IsNullOrWhiteSpace(id));
            Assert.True(System.Guid.TryParse(id, out _));
        }

        [Fact]
        public void SetCorrelationId_WithEmptyString_GeneratesNewId()
        {
            var service = new CorrelationIdService();

            service.SetCorrelationId(" ");
            var id = service.GetCorrelationId();

            Assert.False(string.IsNullOrWhiteSpace(id));
            Assert.True(System.Guid.TryParse(id, out _));
        }
    }
}
