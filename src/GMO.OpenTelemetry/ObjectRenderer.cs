using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace GMO.OpenTelemetry
{
    /// <summary>
    /// Renders an arbitrary object to JSON for a span tag or event attribute, walking at most
    /// <c>maxDepth</c> levels. Values beyond the limit become null, matching Serilog's
    /// <c>Destructure.ToMaximumDepth</c>, so one setting means the same thing in traces and logs.
    /// </summary>
    /// <remarks>
    /// The graph is copied into dictionaries and lists before either serializer sees it, because
    /// neither limits depth while writing: an EF6 lazy-loading entity would be walked through every
    /// navigation property, querying the database at each step. Entity Framework types are never
    /// walked; their getters do not run.
    /// </remarks>
    internal static class ObjectRenderer
    {
        internal const int DefaultMaxDepth = 3;
        internal const int MaxCollectionItems = 100;

        private static int _maxDepth = DefaultMaxDepth;

        /// <summary>The depth <see cref="ActivityWrapper"/> renders with. Process-wide, because spans are
        /// tagged far from any options instance; set from the options by the tracing and Serilog setup.</summary>
        internal static int MaxDepth => _maxDepth;

        private static int _maxValueLength = 4000;

        /// <summary>The longest string <see cref="ActivityWrapper"/> puts in an event attribute or status
        /// description. New Relic truncates span values past 4,095 characters, reporting each as an
        /// integration error; tags are shortened by the span processor, which cannot reach events.</summary>
        internal static int MaxValueLength => _maxValueLength;

        internal static void Configure(IOpenTelemetryOptions options)
        {
            if (options is OpenTelemetryOptionsBase configured)
                _maxDepth = ResolveDepth(configured.MaxSerializationDepth);
            if (options != null && options.MaxAttributeValueLength > 0)
                _maxValueLength = options.MaxAttributeValueLength;
        }

        /// <summary>A string cut to <see cref="MaxValueLength"/>; anything else unchanged.</summary>
        internal static object? Shorten(object? value) =>
            value is string text ? Shorten(text) : value;

        internal static string? Shorten(string? text) =>
            text == null || text.Length <= _maxValueLength
                ? text
                : text[..LogMessageChunker.FirstChunkLength(text, _maxValueLength)];

        internal static int ResolveDepth(int configured) => configured > 0 ? configured : DefaultMaxDepth;

        internal static object? Render(object? value) => Render(value, _maxDepth);

        internal static object? Render(object? value, int maxDepth)
        {
            if (value == null || IsScalar(value)) return value;
            if (maxDepth <= 0) maxDepth = DefaultMaxDepth;

            try
            {
                var tree = Walk(value, 1, maxDepth, new HashSet<object>(ReferenceComparer.Instance));
                return System.Text.Json.JsonSerializer.Serialize(tree);
            }
            catch
            {
                return value.ToString();
            }
        }

        private static object? Walk(object? value, int depth, int maxDepth, HashSet<object> ancestors)
        {
            if (value == null || depth > maxDepth) return null;
            if (TryRenderLeaf(value, out var leaf)) return leaf;
            if (!ancestors.Add(value)) return null;

            try
            {
                if (value is IDictionary dictionary) return WalkDictionary(dictionary, depth, maxDepth, ancestors);
                if (value is IEnumerable sequence) return WalkSequence(sequence, depth, maxDepth, ancestors);
                return WalkMembers(value, depth, maxDepth, ancestors);
            }
            finally
            {
                ancestors.Remove(value);
            }
        }

        // A value written as itself, as text, or as its type name, never walked into.
        private static bool TryRenderLeaf(object value, out object? rendered)
        {
            if (IsScalar(value))
                rendered = value;
            else if (IsOpaque(value))
                rendered = value.ToString();
            // Enumerating a query runs it against the database; the 100-item cap stops reading rows,
            // not the query.
            else if (IsEntityFrameworkType(value.GetType()) || value is IQueryable)
                rendered = value.GetType().FullName;
            else
                rendered = AsText(value) ?? AsJson(value);

            return rendered != null;
        }

        private static Dictionary<string, object?> WalkDictionary(IDictionary dictionary, int depth, int maxDepth, HashSet<object> ancestors)
        {
            var map = new Dictionary<string, object?>();
            foreach (DictionaryEntry entry in dictionary)
            {
                if (map.Count == MaxCollectionItems) break;
                map[entry.Key.ToString() ?? ""] = Walk(entry.Value, depth + 1, maxDepth, ancestors);
            }
            return map;
        }

        private static List<object?> WalkSequence(IEnumerable sequence, int depth, int maxDepth, HashSet<object> ancestors)
        {
            var items = new List<object?>();
            foreach (var item in sequence)
            {
                if (items.Count == MaxCollectionItems) break;
                items.Add(Walk(item, depth + 1, maxDepth, ancestors));
            }
            return items;
        }

        private static Dictionary<string, object?> WalkMembers(object value, int depth, int maxDepth, HashSet<object> ancestors)
        {
            // At the last level every member is written as null, so its getter is not run.
            var lastLevel = depth == maxDepth;
            var properties = new Dictionary<string, object?>();
            foreach (var member in ReadableMembers(value.GetType()))
            {
                if (lastLevel)
                {
                    if (!member.Omits(null))
                        properties[member.Name] = null;
                    continue;
                }

                if (!TryRead(member, value, out var propertyValue) || member.Omits(propertyValue))
                    continue;

                properties[member.Name] = Walk(propertyValue, depth + 1, maxDepth, ancestors);
            }
            return properties;
        }

        // A throwing getter drops that property only.
        private static bool TryRead(Member member, object owner, out object? value)
        {
            try
            {
                value = member.Property.GetValue(owner);
                return true;
            }
            catch
            {
                value = null;
                return false;
            }
        }

        private static Member[] ReadableMembers(Type type) => MembersByType.GetOrAdd(type, t =>
            [.. t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !IsEntityFrameworkMember(p.PropertyType))
                .Select(p => new Member(p))
                .Where(m => !m.AlwaysIgnored)]);

        private static readonly ConcurrentDictionary<Type, Member[]> MembersByType = new();

        /// <summary>
        /// A readable property with its <c>[JsonIgnore]</c> and <c>[JsonPropertyName]</c>, which the walk
        /// has to apply itself: the serializer only ever sees the copied dictionaries, so it never reads
        /// them. Without the first, a property hidden from JSON - a token, a password - would be written
        /// to span tags; without the second, a renamed property changes the tag's shape.
        /// </summary>
        /// <remarks>
        /// Attributes, and the condition, are read by name: net48 resolves System.Text.Json 4.7.2, whose
        /// attribute has no <c>Condition</c>, and the library takes no Newtonsoft reference.
        /// <c>[JsonConverter]</c> is not applied: a converter runs on the original object, which would
        /// walk it without the depth limit.
        /// </remarks>
        private sealed class Member
        {
            private readonly string? _condition;
            private readonly object? _defaultValue;

            internal Member(PropertyInfo property)
            {
                Property = property;
                Name = property.Name;

                foreach (var attribute in property.GetCustomAttributes(true))
                {
                    var type = attribute.GetType();
                    if (type.FullName == "Newtonsoft.Json.JsonIgnoreAttribute"
                        || type.FullName == "System.Data.Linq.Mapping.AssociationAttribute")
                        _condition = "Always";
                    else if (type.FullName == "System.Text.Json.Serialization.JsonIgnoreAttribute")
                        _condition = type.GetProperty("Condition")?.GetValue(attribute)?.ToString() ?? "Always";
                    else if (type.FullName == "System.Text.Json.Serialization.JsonPropertyNameAttribute"
                            && type.GetProperty("Name")?.GetValue(attribute) is string renamed)
                        Name = renamed;
                }

                if (_condition == "WhenWritingDefault" && property.PropertyType.IsValueType)
                    _defaultValue = Activator.CreateInstance(property.PropertyType);
            }

            internal PropertyInfo Property { get; }

            internal string Name { get; }

            internal bool AlwaysIgnored => _condition == "Always" || _condition == "WhenWriting";

            internal bool Omits(object? value) => _condition switch
            {
                "WhenWritingNull" => value == null,
                "WhenWritingDefault" => value == null || value.Equals(_defaultValue),
                _ => false,
            };
        }

        private static bool IsScalar(object value)
        {
            var type = value.GetType();
            return type.IsPrimitive || type.IsEnum
                || value is string || value is decimal || value is DateTime || value is DateTimeOffset
                || value is TimeSpan || value is Guid;
        }

        // Value types System.Text.Json writes as strings. Rendered here because net6.0 compiles against
        // System.Text.Json 6, which cannot serialize them, and the walk would otherwise expand each into
        // its properties.
        private static string? AsText(object value)
        {
#if NET6_0_OR_GREATER
            if (value is DateOnly date) return date.ToString("O", CultureInfo.InvariantCulture);
            if (value is TimeOnly time) return time.ToString("O", CultureInfo.InvariantCulture);
#endif
#if NET7_0_OR_GREATER
            if (value is Half || value is Int128 || value is UInt128)
                return ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture);
#endif
            if (value.GetType().FullName == "System.Numerics.BigInteger")
                return ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture);
            return null;
        }

        // Already-parsed JSON is bounded and serializes as itself; walked, it renders as its ValueKind.
        private static object? AsJson(object value)
        {
            if (value is System.Text.Json.JsonElement) return value;
            if (value is System.Text.Json.JsonDocument document) return document.RootElement;
            return value.GetType().Namespace == "System.Text.Json.Nodes" ? value : null;
        }

        // Reflection, delegate and stream objects have huge or live graphs and say nothing useful as JSON.
        private static bool IsOpaque(object value) =>
            value is Type || value is MemberInfo || value is Assembly || value is Delegate
            || value is System.IO.Stream || value is Uri || value is Version;

        private static bool IsEntityFrameworkMember(Type type) =>
            IsEntityFrameworkType(type)
            || (type.IsGenericType && type.GetGenericArguments().Any(IsEntityFrameworkType));

        private static readonly ConcurrentDictionary<Type, bool> EntityFrameworkTypes = new();

        /// <summary>
        /// Matched by name, so the library needs no Entity Framework reference: anything in an EF6 or EF4
        /// namespace (contexts, sets, entries, lazy-loading proxies, entities and relationship
        /// containers), anything in EF Core's, and EF Core's Castle proxies. A context or set is caught
        /// through its base type, so a subclass in the host's own namespace is matched too.
        /// </summary>
        internal static bool IsEntityFrameworkType(Type type) => EntityFrameworkTypes.GetOrAdd(type, t =>
        {
            for (Type? current = t; current != null; current = current.BaseType)
            {
                var ns = current.Namespace;
                if (ns != null && (ns.StartsWith("System.Data.Entity", StringComparison.Ordinal)
                        || ns.StartsWith("System.Data.Objects", StringComparison.Ordinal)
                        || ns.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                        || ns.StartsWith("Castle.Proxies", StringComparison.Ordinal)))
                    return true;
            }

            return t.GetInterfaces().Any(i => i.Name == "IEntityWithRelationships" || i.Name == "IEntityWithChangeTracker");
        });

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new();

            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
