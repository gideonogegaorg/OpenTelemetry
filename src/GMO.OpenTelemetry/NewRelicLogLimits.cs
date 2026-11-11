using System.Text.Json;

namespace GMO.OpenTelemetry
{
    /// <summary>What New Relic does to a log record on ingest, shared by the Serilog and log4net paths.</summary>
    internal static class NewRelicLogLimits
    {
        // New Relic keeps the first 64 entries of an array and discards the rest.
        internal const int MaxArrayLength = 64;

        // Put before a message that is a JSON object with more fields than the record has room for, so
        // New Relic keeps it as text instead of expanding it and dropping the record.
        internal const string JsonMessagePrefix = "JSON: ";

        // Chunks after the first carry only context values this short: every chunk copying a large value
        // multiplies it into the export request, and New Relic rejects a request over 1 MB whole.
        internal const int ChunkContextValueLength = 256;

        /// <summary>
        /// The attributes New Relic makes of a string that is a JSON object - it parses one in the message
        /// or in any string attribute, replacing the string with one attribute per field - or null when
        /// the string stays a single attribute.
        /// </summary>
        internal static int? JsonObjectFields(string? text)
        {
            if (text == null)
                return null;

            var start = 0;
            while (start < text.Length && char.IsWhiteSpace(text[start]))
                start++;
            if (start == text.Length || text[start] != '{' || text[text.Length - 1] != '}')
                return null;

            try
            {
                using var document = JsonDocument.Parse(text);
                return Fields(document.RootElement);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // Nested objects flatten to dotted names; an array stays one attribute; a null is not stored.
        private static int Fields(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var fields = 0;
                    foreach (var property in element.EnumerateObject())
                        fields += Fields(property.Value);
                    return fields;
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return 0;
                default:
                    return 1;
            }
        }
    }
}
