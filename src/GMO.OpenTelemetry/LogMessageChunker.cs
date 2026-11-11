using System;
using System.Collections.Generic;

namespace GMO.OpenTelemetry
{
    internal static class LogMessageChunker
    {
        internal static List<string> Chunk(string? message, int maxLength)
        {
            var chunks = new List<string>();

            if (Strings.IsNullOrEmpty(message))
            {
                chunks.Add(string.Empty);
                return chunks;
            }

            if (maxLength <= 0 || message.Length <= maxLength)
            {
                chunks.Add(message);
                return chunks;
            }

            var start = 0;
            while (start < message.Length)
            {
                var length = BoundaryLength(message, start, maxLength);
                chunks.Add(message.Substring(start, length));
                start += length;
            }

            return chunks;
        }

        // For callers that truncate rather than split. Must agree with Chunk on every input, including
        // a non-positive maxLength, or the natural Substring pairing blanks the value.
        internal static int FirstChunkLength(string message, int maxLength)
        {
            if (string.IsNullOrEmpty(message)) return 0;
            if (maxLength <= 0 || message.Length <= maxLength) return message.Length;
            return BoundaryLength(message, 0, maxLength);
        }

        // Never splits a surrogate pair, shrinking to stay within maxLength. Sole exception: maxLength 1
        // against a pair returns 2. Malformed input is passed through, not repaired.
        private static int BoundaryLength(string message, int start, int maxLength)
        {
            var length = Math.Min(maxLength, message.Length - start);

            if (start + length < message.Length && char.IsHighSurrogate(message[start + length - 1]))
            {
                if (length > 1) length--;
                else if (char.IsLowSurrogate(message[start + 1])) length = 2;
            }

            return length;
        }
    }
}
