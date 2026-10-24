using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeLogic.Core.Configuration;

/// <summary>
/// Helpers for working out what changed between two config snapshots — typically
/// <see cref="ConfigChangedEventArgs.OldValue"/> and <see cref="ConfigChangedEventArgs.NewValue"/>.
/// Values are compared by their JSON form, so config POCOs don't need to implement <c>Equals</c>.
/// </summary>
public static class ConfigDiff
{
    /// <summary>
    /// Compares two keyed instance maps (e.g. <c>Databases</c>) and splits the keys into
    /// added, removed, changed, and unchanged. Use it to touch only the instances that changed.
    /// </summary>
    /// <param name="old">Previous map (null = empty).</param>
    /// <param name="new">Current map (null = empty).</param>
    /// <param name="comparer">Key comparer. Default: ordinal.</param>
    public static KeyedDiff<TValue> Keyed<TValue>(
        IReadOnlyDictionary<string, TValue>? old,
        IReadOnlyDictionary<string, TValue>? @new,
        StringComparer? comparer = null)
    {
        comparer ??= StringComparer.Ordinal;
        var added = new Dictionary<string, TValue>(comparer);
        var removed = new Dictionary<string, TValue>(comparer);
        var changed = new Dictionary<string, ConfigValueChange<TValue>>(comparer);
        var unchanged = new Dictionary<string, TValue>(comparer);

        var oldMap = new Dictionary<string, TValue>(comparer);
        if (old is not null)
            foreach (var (key, value) in old) oldMap[key] = value;

        if (@new is not null)
        {
            foreach (var (key, value) in @new)
            {
                if (!oldMap.Remove(key, out var previous))
                    added[key] = value;
                else if (ValueEquals(previous, value))
                    unchanged[key] = value;
                else
                    changed[key] = new ConfigValueChange<TValue>(previous, value);
            }
        }

        foreach (var (key, value) in oldMap)
            removed[key] = value;

        return new KeyedDiff<TValue>(added, removed, changed, unchanged);
    }

    /// <summary>True when both values serialize to the same JSON.</summary>
    public static bool ValueEquals<TValue>(TValue? a, TValue? b) =>
        JsonNode.DeepEquals(ToNode(a), ToNode(b));

    /// <summary>
    /// Returns the dotted paths (same format as <see cref="ConfigField.Path"/>, e.g.
    /// <c>databases.Main.host</c>) of every leaf value that differs. Lists are compared as a whole.
    /// </summary>
    public static IReadOnlyList<string> ChangedProperties(object? old, object? @new) =>
        DiffPaths(ToNode(old), ToNode(@new)).Select(p => string.Join('.', p)).ToList();

    /// <summary>
    /// True when any of <paramref name="paths"/> points at a property marked
    /// <see cref="ConfigFieldAttribute.RequiresRestart"/> on <paramref name="configType"/>.
    /// Dictionary keys in the path are skipped over.
    /// </summary>
    public static bool RequiresRestart(Type configType, IEnumerable<string> paths) =>
        paths.Any(p => PathRequiresRestart(configType, p.Split('.')));

    // ─── Internals (shared with ConfigurationManager) ─────────────────────────

    internal static JsonNode? ToNode<TValue>(TValue? value) =>
        value is null ? null : JsonSerializer.SerializeToNode(value, value.GetType(), ConfigurationManager.JsonOptions);

    /// <summary>Leaf paths that differ between two JSON trees. Objects recurse; everything else is a leaf.</summary>
    internal static List<string[]> DiffPaths(JsonNode? a, JsonNode? b)
    {
        var result = new List<string[]>();
        Walk(a, b, [], result);
        return result;

        static void Walk(JsonNode? x, JsonNode? y, List<string> path, List<string[]> acc)
        {
            if (x is JsonObject ox && y is JsonObject oy)
            {
                foreach (var key in ox.Select(p => p.Key).Union(oy.Select(p => p.Key)))
                {
                    path.Add(key);
                    Walk(ox[key], oy[key], path, acc);
                    path.RemoveAt(path.Count - 1);
                }
                return;
            }

            if (!JsonNode.DeepEquals(x, y))
                acc.Add(path.ToArray());
        }
    }

    private static bool PathRequiresRestart(Type type, string[] segments)
    {
        var current = type;
        for (var i = 0; i < segments.Length; i++)
        {
            var prop = current.GetProperty(segments[i],
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop is null) return false;
            if (prop.GetCustomAttribute<ConfigFieldAttribute>()?.RequiresRestart == true) return true;

            var pt = prop.PropertyType;
            if (pt.IsGenericType && typeof(IDictionary).IsAssignableFrom(pt) && pt.GetGenericArguments().Length == 2)
            {
                current = pt.GetGenericArguments()[1];
                i++; // skip the dictionary key segment
                continue;
            }

            if (!pt.IsClass || pt == typeof(string)) return false;
            current = pt;
        }
        return false;
    }
}

/// <summary>An old/new pair for a keyed instance whose values changed.</summary>
public readonly record struct ConfigValueChange<TValue>(TValue Old, TValue New);

/// <summary>Result of <see cref="ConfigDiff.Keyed{TValue}"/>.</summary>
public sealed class KeyedDiff<TValue>
{
    internal KeyedDiff(
        IReadOnlyDictionary<string, TValue> added,
        IReadOnlyDictionary<string, TValue> removed,
        IReadOnlyDictionary<string, ConfigValueChange<TValue>> changed,
        IReadOnlyDictionary<string, TValue> unchanged)
    {
        Added = added;
        Removed = removed;
        Changed = changed;
        Unchanged = unchanged;
    }

    /// <summary>Keys present only in the new map.</summary>
    public IReadOnlyDictionary<string, TValue> Added { get; }

    /// <summary>Keys present only in the old map (values are the old ones).</summary>
    public IReadOnlyDictionary<string, TValue> Removed { get; }

    /// <summary>Keys present in both maps with different values.</summary>
    public IReadOnlyDictionary<string, ConfigValueChange<TValue>> Changed { get; }

    /// <summary>Keys present in both maps with identical values.</summary>
    public IReadOnlyDictionary<string, TValue> Unchanged { get; }

    /// <summary>True when anything was added, removed, or changed.</summary>
    public bool HasChanges => Added.Count > 0 || Removed.Count > 0 || Changed.Count > 0;
}
