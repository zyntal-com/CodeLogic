using System.Globalization;

namespace CodeLogic;

/// <summary>
/// Immutable snapshot of application startup parameters. Command-line values
/// take precedence over environment variables.
/// </summary>
internal sealed class StartupParameterStore
{
    private static readonly HashSet<string> ReservedFlags =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "generate-configs", "generate-configs-force", "dry-run", "version", "info", "health"
    ];

    private readonly IReadOnlyDictionary<string, string> _commandLine;
    private readonly IReadOnlyDictionary<string, string> _environment;

    private StartupParameterStore(
        IReadOnlyDictionary<string, string> commandLine,
        IReadOnlyDictionary<string, string> environment)
    {
        _commandLine = commandLine;
        _environment = new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase);
    }

    public static StartupParameterStore Capture()
    {
        Dictionary<string, string> environment = [with(StringComparer.OrdinalIgnoreCase)];
        foreach (System.Collections.DictionaryEntry item in Environment.GetEnvironmentVariables())
        {
            if (item.Key is string key && item.Value is string value)
                environment[key] = value;
        }

        return From(Environment.GetCommandLineArgs().Skip(1), environment);
    }

    internal static StartupParameterStore From(
        IEnumerable<string> args,
        IReadOnlyDictionary<string, string> environment)
    {
        Dictionary<string, string> commandLine = [with(StringComparer.OrdinalIgnoreCase)];
        var values = args.ToArray();

        for (var i = 0; i < values.Length; i++)
        {
            var argument = values[i];
            if (!argument.StartsWith("--", StringComparison.Ordinal) || argument.Length == 2)
                continue;

            var content = argument[2..];
            var equals = content.IndexOf('=');
            var name = equals >= 0 ? content[..equals] : content;
            if (string.IsNullOrWhiteSpace(name) || ReservedFlags.Contains(name))
                continue;

            string value;
            if (equals >= 0)
            {
                value = content[(equals + 1)..];
            }
            else if (i + 1 < values.Length && !values[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = values[++i];
            }
            else
            {
                // A valueless application parameter is useful for bools and is
                // otherwise reported as an invalid conversion by the accessor.
                value = "true";
            }

            // Assignment intentionally replaces an earlier occurrence: the final CLI value wins.
            commandLine[name] = value;
        }

        return new StartupParameterStore(commandLine, environment);
    }

    public T? Get<T>(string name, T? defaultValue = default)
    {
        ValidateName(name);
        if (!TryGetRawValue(name, out var rawValue))
            return defaultValue;

        return Convert<T>(name, rawValue);
    }

    public T GetRequired<T>(string name)
    {
        ValidateName(name);
        if (!TryGetRawValue(name, out var rawValue))
            throw new InvalidOperationException($"Required startup parameter '{name}' was not supplied.");

        return Convert<T>(name, rawValue)!;
    }

    private bool TryGetRawValue(string name, out string value)
    {
        if (ReservedFlags.Contains(name))
        {
            value = string.Empty;
            return false;
        }

        if (_commandLine.TryGetValue(name, out value!))
            return true;

        return _environment.TryGetValue(ToEnvironmentName(name), out value!);
    }

    private static T? Convert<T>(string name, string rawValue)
    {
        var requestedType = typeof(T);
        var targetType = Nullable.GetUnderlyingType(requestedType) ?? requestedType;

        try
        {
            object converted;
            if (targetType == typeof(string))
                converted = rawValue;
            else if (targetType == typeof(bool))
                converted = bool.Parse(rawValue);
            else if (targetType.IsEnum)
                converted = Enum.Parse(targetType, rawValue, ignoreCase: true);
            else if (targetType == typeof(Guid))
                converted = Guid.Parse(rawValue);
            else if (targetType == typeof(TimeSpan))
                converted = TimeSpan.Parse(rawValue, CultureInfo.InvariantCulture);
            else if (targetType == typeof(byte))
                converted = byte.Parse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture);
            else if (targetType == typeof(sbyte))
                converted = sbyte.Parse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture);
            else if (targetType == typeof(short))
                converted = short.Parse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture);
            else if (targetType == typeof(ushort))
                converted = ushort.Parse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture);
            else if (targetType == typeof(int))
                converted = int.Parse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture);
            else if (targetType == typeof(uint))
                converted = uint.Parse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture);
            else if (targetType == typeof(long))
                converted = long.Parse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture);
            else if (targetType == typeof(ulong))
                converted = ulong.Parse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture);
            else if (targetType == typeof(nint))
                converted = nint.Parse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture);
            else if (targetType == typeof(nuint))
                converted = nuint.Parse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture);
            else if (targetType == typeof(float))
                converted = float.Parse(rawValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
            else if (targetType == typeof(double))
                converted = double.Parse(rawValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
            else if (targetType == typeof(decimal))
                converted = decimal.Parse(rawValue, NumberStyles.Number, CultureInfo.InvariantCulture);
            else
                throw new NotSupportedException($"Type '{targetType.Name}' is not a supported startup parameter type.");

            return (T)converted;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            // Do not include rawValue: startup values commonly contain secrets.
            throw new InvalidOperationException(
                $"Startup parameter '{name}' could not be converted to '{targetType.Name}'.");
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Startup parameter name cannot be empty.", nameof(name));
    }

    private static string ToEnvironmentName(string name) => string.Concat(name.Select(c =>
        char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_'));
}
