using System.Diagnostics.CodeAnalysis;

namespace GMO.OpenTelemetry
{
    // net48's string.IsNullOrEmpty/IsNullOrWhiteSpace carry no [NotNullWhen(false)], so a null check through them
    // leaves the value maybe-null there; these wrappers give every target the same flow analysis.
    internal static class Strings
    {
        public static bool IsNullOrEmpty([NotNullWhen(false)] string? value) => string.IsNullOrEmpty(value);

        public static bool IsNullOrWhiteSpace([NotNullWhen(false)] string? value) => string.IsNullOrWhiteSpace(value);
    }
}
