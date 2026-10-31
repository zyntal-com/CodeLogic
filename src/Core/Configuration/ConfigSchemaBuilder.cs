using System.Collections;
using System.Reflection;

namespace CodeLogic.Core.Configuration;

/// <summary>
/// Reflects over a configuration type and produces a flat <see cref="ConfigSchema"/>
/// describing every editable field. Handles nested objects, dictionaries of
/// sub-configs, enums, and primitive types. Unknown complex types are represented
/// as opaque "object" fields that UIs can skip or delegate to a custom editor.
/// </summary>
internal static class ConfigSchemaBuilder
{
    /// <summary>
    /// Build a <see cref="ConfigSchema"/> for the given registered type and
    /// currently-loaded instance. Pass <paramref name="instance"/> = null to
    /// produce a defaults-only schema (fresh instance is used for current values).
    /// </summary>
    public static ConfigSchema Build(
        Type sectionType,
        string sectionName,
        object? instance,
        bool includeSecrets)
    {
        var sectionAttr = sectionType.GetCustomAttribute<ConfigSectionAttribute>();
        var current = instance ?? Activator.CreateInstance(sectionType);
        var defaults = Activator.CreateInstance(sectionType);

        var fields = new List<ConfigField>();
        AppendFields(sectionType, current, defaults, path: "", group: null, groupCollapsed: false, includeSecrets, fields);

        return new ConfigSchema
        {
            SectionName = sectionName,
            Title = sectionType.Name,
            Version = sectionAttr?.Version ?? 1,
            Fields = fields
        };
    }

    private static void AppendFields(
        Type type,
        object? currentInstance,
        object? defaultInstance,
        string path,
        string? group,
        bool groupCollapsed,
        bool includeSecrets,
        List<ConfigField> accumulator)
    {
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
            .ToArray();

        foreach (var prop in props)
        {
            var attr = prop.GetCustomAttribute<ConfigFieldAttribute>();
            var fieldPath = string.IsNullOrEmpty(path) ? ToCamelCase(prop.Name) : $"{path}.{ToCamelCase(prop.Name)}";
            var propType = prop.PropertyType;
            var currentValue = currentInstance is null ? null : prop.GetValue(currentInstance);
            var defaultValue = defaultInstance is null ? null : prop.GetValue(defaultInstance);
            var fieldGroup = attr?.Group ?? group;
            var fieldCollapsed = attr?.Collapsed ?? groupCollapsed;

            if (IsPrimitiveLike(propType))
            {
                accumulator.Add(BuildPrimitiveField(prop, attr, fieldPath, propType, currentValue, defaultValue, fieldGroup, fieldCollapsed, includeSecrets));
                continue;
            }

            if (propType.IsEnum)
            {
                accumulator.Add(BuildEnumField(prop, attr, fieldPath, propType, currentValue, defaultValue, fieldGroup, fieldCollapsed, includeSecrets));
                continue;
            }

            if (IsDictionaryOfConfig(propType, out var valueType) && valueType is not null)
            {
                // Dictionary<string, X> — iterate current entries. For each entry, recurse X with path "{fieldPath}.{key}".
                if (currentValue is IDictionary dict)
                {
                    foreach (var key in dict.Keys)
                    {
                        if (key is null) continue;
                        var keyStr = key.ToString() ?? "";
                        var entryPath = $"{fieldPath}.{keyStr}";
                        AppendFields(valueType, dict[key], Activator.CreateInstance(valueType), entryPath, fieldGroup, fieldCollapsed, includeSecrets, accumulator);
                    }
                }
                continue;
            }

            if (IsListOfObjects(propType, out var elementType) && elementType is not null)
            {
                // List<X> of objects — recurse each entry with path "{fieldPath}.{index}".
                if (currentValue is IList list)
                {
                    for (var i = 0; i < list.Count; i++)
                    {
                        if (list[i] is null) continue;
                        AppendFields(elementType, list[i], Activator.CreateInstance(elementType), $"{fieldPath}.{i}", fieldGroup, fieldCollapsed, includeSecrets, accumulator);
                    }
                }
                continue;
            }

            if (IsListOfPrimitive(propType))
            {
                // List of primitives renders as a single textarea (one value per line) — one field.
                var listSecret = attr?.Secret ?? false;
                accumulator.Add(new ConfigField
                {
                    Path = fieldPath,
                    Label = attr?.Label ?? Humanize(prop.Name),
                    Description = attr?.Description,
                    InputType = ConfigInputType.Textarea,
                    TypeHint = "list",
                    Required = attr?.Required ?? false,
                    Secret = attr?.Secret ?? false,
                    RequiresRestart = attr?.RequiresRestart ?? false,
                    Placeholder = attr?.Placeholder,
                    MaxLength = attr?.MaxLength > 0 ? attr.MaxLength : null,
                    Group = fieldGroup,
                    GroupCollapsed = fieldCollapsed,
                    Order = attr?.Order ?? 0,
                    DefaultValue = listSecret && !includeSecrets ? MaskList(defaultValue) : defaultValue,
                    CurrentValue = listSecret && !includeSecrets ? MaskList(currentValue) : currentValue
                });
                continue;
            }

            // Nested complex object — recurse with dotted path.
            if (propType.IsClass && propType != typeof(string))
            {
                AppendFields(propType, currentValue, defaultValue ?? Activator.CreateInstance(propType), fieldPath, fieldGroup, fieldCollapsed, includeSecrets, accumulator);
                continue;
            }

            // Fallback: opaque object, skip (UI can't render it).
        }
    }

    private static ConfigField BuildPrimitiveField(
        PropertyInfo prop,
        ConfigFieldAttribute? attr,
        string path,
        Type propType,
        object? currentValue,
        object? defaultValue,
        string? group,
        bool groupCollapsed,
        bool includeSecrets)
    {
        var isSecret = attr?.Secret ?? false;
        var inputType = attr?.InputType ?? ConfigInputType.Auto;
        if (inputType == ConfigInputType.Auto)
        {
            inputType = isSecret
                ? ConfigInputType.Password
                : propType == typeof(bool) ? ConfigInputType.Checkbox
                : IsNumeric(propType) ? ConfigInputType.Number
                : ConfigInputType.Text;
        }

        var typeHint = TypeHintFor(propType);
        var mask = isSecret && !includeSecrets;

        return new ConfigField
        {
            Path = path,
            Label = attr?.Label ?? Humanize(prop.Name),
            Description = attr?.Description,
            InputType = inputType,
            TypeHint = typeHint,
            Required = attr?.Required ?? false,
            Secret = isSecret,
            RequiresRestart = attr?.RequiresRestart ?? false,
            Placeholder = attr?.Placeholder,
            AllowedValues = attr?.AllowedValues is { Length: > 0 } av ? av.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : null,
            Min = attr is not null && !double.IsNaN(attr.Min) ? attr.Min : null,
            Max = attr is not null && !double.IsNaN(attr.Max) ? attr.Max : null,
            MaxLength = attr?.MaxLength > 0 ? attr.MaxLength : null,
            Group = group,
            GroupCollapsed = groupCollapsed,
            Order = attr?.Order ?? 0,
            DefaultValue = mask ? MaskScalar(defaultValue) : defaultValue,
            CurrentValue = mask ? MaskScalar(currentValue) : currentValue
        };
    }

    /// <summary>
    /// Every set value becomes <see cref="ConfigField.SecretMask"/>, whatever its type; <see langword="null"/>
    /// and the empty string are kept so a UI can still tell "not set" apart. Non-string secrets echoed
    /// back as the mask are restored by <see cref="ConfigurationManager.UpdateSectionAsync"/> before the
    /// JSON is deserialized.
    /// </summary>
    private static object? MaskScalar(object? value) => value switch
    {
        null => null,
        string { Length: 0 } => value,
        _ => ConfigField.SecretMask
    };

    /// <summary>
    /// Copies a list of primitives with every set entry replaced by
    /// <see cref="ConfigField.SecretMask"/>. Clients echo the masked entries back and
    /// <see cref="ConfigurationManager.UpdateSectionAsync"/> restores them by position.
    /// </summary>
    private static List<object?>? MaskList(object? value)
    {
        if (value is not IList list) return null;
        var masked = new List<object?>(list.Count);
        foreach (var entry in list)
            masked.Add(MaskScalar(entry));
        return masked;
    }

    /// <summary>True for <c>List&lt;string&gt;</c> / <c>IList&lt;string&gt;</c> / <c>IReadOnlyList&lt;string&gt;</c>.</summary>
    internal static bool IsListOfString(Type t) =>
        IsListOfPrimitive(t) && t.GetGenericArguments()[0] == typeof(string);

    private static ConfigField BuildEnumField(
        PropertyInfo prop,
        ConfigFieldAttribute? attr,
        string path,
        Type enumType,
        object? currentValue,
        object? defaultValue,
        string? group,
        bool groupCollapsed,
        bool includeSecrets)
    {
        var names = Enum.GetNames(enumType);
        var isSecret = attr?.Secret ?? false;
        var mask = isSecret && !includeSecrets;
        return new ConfigField
        {
            Path = path,
            Label = attr?.Label ?? Humanize(prop.Name),
            Description = attr?.Description,
            InputType = ConfigInputType.Select,
            TypeHint = $"enum:{string.Join(',', names)}",
            Required = attr?.Required ?? false,
            Secret = isSecret,
            RequiresRestart = attr?.RequiresRestart ?? false,
            AllowedValues = mask ? null : names,
            Group = group,
            GroupCollapsed = groupCollapsed,
            Order = attr?.Order ?? 0,
            DefaultValue = mask ? MaskScalar(defaultValue) : defaultValue?.ToString(),
            CurrentValue = mask ? MaskScalar(currentValue) : currentValue?.ToString()
        };
    }

    /// <summary>
    /// True for a scalar property that is not a <c>string</c>: numbers, <c>bool</c>, <c>Guid</c>, <c>DateTime</c>
    /// and enums. A masked secret of such a type cannot be deserialized as-is, so it is restored on the JSON.
    /// </summary>
    internal static bool IsNonStringScalar(Type t)
    {
        var under = Nullable.GetUnderlyingType(t) ?? t;
        return under != typeof(string) && (IsPrimitiveLike(under) || under.IsEnum);
    }

    internal static bool IsPrimitiveLike(Type t)
    {
        var under = Nullable.GetUnderlyingType(t) ?? t;
        return under.IsPrimitive
            || under == typeof(string)
            || under == typeof(decimal)
            || under == typeof(DateTime)
            || under == typeof(Guid);
    }

    private static bool IsNumeric(Type t)
    {
        var under = Nullable.GetUnderlyingType(t) ?? t;
        return under == typeof(int) || under == typeof(long) || under == typeof(short)
            || under == typeof(double) || under == typeof(float) || under == typeof(decimal)
            || under == typeof(uint) || under == typeof(ulong) || under == typeof(ushort)
            || under == typeof(byte) || under == typeof(sbyte);
    }

    private static bool IsDictionaryOfConfig(Type t, out Type? valueType)
    {
        valueType = null;
        if (!t.IsGenericType) return false;
        var def = t.GetGenericTypeDefinition();
        if (def != typeof(Dictionary<,>) && def != typeof(IDictionary<,>)) return false;
        var args = t.GetGenericArguments();
        if (args[0] != typeof(string)) return false;
        valueType = args[1];
        return true;
    }

    internal static bool IsListOfPrimitive(Type t)
    {
        if (!t.IsGenericType) return false;
        var def = t.GetGenericTypeDefinition();
        if (def != typeof(List<>) && def != typeof(IList<>) && def != typeof(IReadOnlyList<>)) return false;
        return IsPrimitiveLike(t.GetGenericArguments()[0]);
    }

    /// <summary>True for <c>List&lt;X&gt;</c> / <c>IList&lt;X&gt;</c> / <c>IReadOnlyList&lt;X&gt;</c> of config objects.</summary>
    internal static bool IsListOfObjects(Type t, out Type? elementType)
    {
        elementType = null;
        if (!t.IsGenericType) return false;
        var def = t.GetGenericTypeDefinition();
        if (def != typeof(List<>) && def != typeof(IList<>) && def != typeof(IReadOnlyList<>)) return false;
        var arg = t.GetGenericArguments()[0];
        if (IsPrimitiveLike(arg) || arg.IsEnum || !arg.IsClass) return false;
        elementType = arg;
        return true;
    }

    /// <summary>The string property that identifies a list entry: <c>Id</c>, <c>Name</c>, <c>Key</c>, else the first <c>…Id</c>.</summary>
    internal static PropertyInfo? FindKeyProperty(Type elementType)
    {
        var strings = elementType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.CanRead && p.GetIndexParameters().Length == 0)
            .ToList();
        return strings.FirstOrDefault(p => p.Name is "Id" or "Name" or "Key")
            ?? strings.FirstOrDefault(p => p.Name.EndsWith("Id", StringComparison.Ordinal));
    }

    private static string TypeHintFor(Type t)
    {
        var under = Nullable.GetUnderlyingType(t) ?? t;
        if (under == typeof(string)) return "string";
        if (under == typeof(bool)) return "bool";
        if (under == typeof(int) || under == typeof(short) || under == typeof(byte)) return "int";
        if (under == typeof(long) || under == typeof(uint)) return "long";
        if (under == typeof(float) || under == typeof(double) || under == typeof(decimal)) return "double";
        if (under == typeof(DateTime)) return "datetime";
        if (under == typeof(Guid)) return "guid";
        return "object";
    }

    private static string ToCamelCase(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];

    /// <summary>Turn "MinPoolSize" into "Min Pool Size".</summary>
    private static string Humanize(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new System.Text.StringBuilder(name.Length + 4);
        sb.Append(name[0]);
        for (var i = 1; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && !char.IsUpper(name[i - 1]))
                sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
