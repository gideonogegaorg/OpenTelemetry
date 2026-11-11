using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class LogMessageChunkerTests
    {
        private static readonly string[] EvenSplit = { "01234", "56789" };
        private static readonly string[] RemainderSplit = { "01234", "56789", "ab" };

        // Astral-plane code points: two UTF-16 units each, so they straddle chunk boundaries.
        private static readonly string Astral1 = char.ConvertFromUtf32(0x1F600);
        private static readonly string Astral2 = char.ConvertFromUtf32(0x1F601);

        [Fact]
        public void Chunk_EmptyMessage_ReturnsSingleEmptyChunk()
        {
            Assert.Equal(string.Empty, Assert.Single(LogMessageChunker.Chunk(string.Empty, 10)));
        }

        [Fact]
        public void Chunk_NullMessage_ReturnsSingleEmptyChunk()
        {
            Assert.Equal(string.Empty, Assert.Single(LogMessageChunker.Chunk(null, 10)));
        }

        [Fact]
        public void Chunk_MaxLengthZeroOrNegative_ReturnsWholeMessage()
        {
            Assert.Equal("abcdef", Assert.Single(LogMessageChunker.Chunk("abcdef", 0)));
            Assert.Equal("abcdef", Assert.Single(LogMessageChunker.Chunk("abcdef", -1)));
        }

        [Fact]
        public void Chunk_MessageUnderLimit_ReturnsSingleChunk()
        {
            Assert.Equal("abc", Assert.Single(LogMessageChunker.Chunk("abc", 10)));
        }

        [Fact]
        public void Chunk_ExactMultiple_SplitsEvenly()
        {
            var chunks = LogMessageChunker.Chunk("0123456789", 5);
            Assert.Equal(EvenSplit, chunks);
        }

        [Fact]
        public void Chunk_Remainder_LastChunkIsShorter()
        {
            var chunks = LogMessageChunker.Chunk("0123456789ab", 5);
            Assert.Equal(RemainderSplit, chunks);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(7)]
        public void Chunk_AstralPlaneCharacters_NeverSplitsSurrogatePair(int maxLength)
        {
            var message = string.Concat(Enumerable.Repeat(Astral1 + Astral2, 4)) + "tail";

            var chunks = LogMessageChunker.Chunk(message, maxLength);

            Assert.Equal(message, string.Concat(chunks));
            Assert.All(chunks, chunk =>
            {
                Assert.NotEmpty(chunk);
                Assert.False(char.IsHighSurrogate(chunk[chunk.Length - 1]),
                    $"chunk '{chunk}' ends with an unpaired high surrogate");
                Assert.False(char.IsLowSurrogate(chunk[0]),
                    $"chunk '{chunk}' starts with an unpaired low surrogate");
            });
        }

        [Fact]
        public void Chunk_SurrogateAtBoundary_ShrinksRatherThanExceedingMaxLength()
        {
            // maxLength 3 would otherwise cut the pair after its high surrogate. The boundary moves
            // back so the pair starts the next chunk, keeping every chunk within maxLength.
            var message = "ab" + Astral1 + "cd";

            var chunks = LogMessageChunker.Chunk(message, 3);

            Assert.Equal(message, string.Concat(chunks));
            Assert.Equal("ab", chunks[0]);
            Assert.All(chunks, chunk => Assert.True(chunk.Length <= 3, $"chunk '{chunk}' exceeds maxLength"));
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(130)]
        public void Chunk_AstralPlaneCharacters_NeverExceedMaxLength(int maxLength)
        {
            // Callers size maxLength against the APM per-record cap, so overshooting breaks the contract.
            var message = string.Concat(Enumerable.Repeat(Astral1 + Astral2, 40)) + "tail";

            var chunks = LogMessageChunker.Chunk(message, maxLength);

            Assert.Equal(message, string.Concat(chunks));
            Assert.All(chunks, chunk => Assert.True(chunk.Length <= maxLength,
                $"chunk of {chunk.Length} exceeds maxLength {maxLength}"));
        }

        [Fact]
        public void Chunk_MaxLengthOne_TakesBothUnitsBecauseThereIsNoRoomToShrink()
        {
            var chunks = LogMessageChunker.Chunk(Astral1 + "x", 1);

            Assert.Equal(Astral1 + "x", string.Concat(chunks));
            Assert.Equal(Astral1, chunks[0]);
            // The documented exception to the "never exceeds maxLength" rule: one UTF-16 unit cannot
            // represent a surrogate pair, and losing the character outright is worse than one unit over.
            Assert.Equal(2, chunks[0].Length);
        }

        [Theory]
        [InlineData(2)]
        [InlineData(5)]
        [InlineData(130)]
        public void FirstChunkLength_MatchesFirstChunk(int maxLength)
        {
            var message = Astral1 + "abc" + Astral2 + "def";

            var length = LogMessageChunker.FirstChunkLength(message, maxLength);

            Assert.Equal(LogMessageChunker.Chunk(message, maxLength)[0].Length, length);
        }

        // Malformed input passes through: a high surrogate with no low one after it is not a pair, so
        // maxLength 1 is not widened to 2 for it.
        [Fact]
        public void LoneHighSurrogate_AtMaxLengthOne_StaysOneUnit()
        {
            const string loneHigh = "\uD83D";
            var message = loneHigh + "xy";

            var chunks = LogMessageChunker.Chunk(message, 1);

            Assert.Equal(new[] { loneHigh, "x", "y" }, chunks);
            Assert.Equal(1, LogMessageChunker.FirstChunkLength(message, 1));
        }

        [Fact]
        public void FirstChunkLength_AgreesWithChunkOnDegenerateInputs()
        {
            Assert.Equal(0, LogMessageChunker.FirstChunkLength(string.Empty, 10));

            // Non-positive maxLength means "no chunking" to Chunk, so the first chunk is the whole
            // message. Returning 0 here would make the natural Substring pairing blank the value.
            Assert.Equal(3, LogMessageChunker.FirstChunkLength("abc", 0));
            Assert.Equal(3, LogMessageChunker.FirstChunkLength("abc", -1));
            Assert.Equal(LogMessageChunker.Chunk("abc", 0)[0].Length, LogMessageChunker.FirstChunkLength("abc", 0));
        }

        [Fact]
        public void Chunk_MalformedLoneHighSurrogates_RoundTripUnchanged()
        {
            // No chunk boundary can repair input that is already invalid UTF-16; the contract is that
            // the chunker passes it through rather than silently altering it.
            var message = "\uD83D\uD83D\uD83Dtail";

            var chunks = LogMessageChunker.Chunk(message, 2);

            Assert.Equal(message, string.Concat(chunks));
        }

        [Fact]
        public void Chunk_AsciiMessage_RoundTrips()
        {
            var message = new string('x', 1000) + new string('y', 137);
            var chunks = LogMessageChunker.Chunk(message, 130);
            Assert.Equal(message, string.Concat(chunks));
            Assert.All(chunks, chunk => Assert.True(chunk.Length <= 130));
        }
    }
}
