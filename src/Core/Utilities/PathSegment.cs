namespace CodeLogic.Core.Utilities;

/// <summary>
/// Checks names that become part of a file or directory name (library and plugin IDs, config
/// section names) so they cannot leave their directory or alias another file.
/// </summary>
internal static class PathSegment
{
    // Invalid on Windows; rejected everywhere so a name means the same file on every platform.
    private static readonly char[] _invalid = [.. Path.GetInvalidFileNameChars(), '/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// True when <paramref name="name"/> is a single, portable path segment: not empty, no path
    /// separators, no <c>..</c>, no characters invalid in file names (control characters, <c>:</c>,
    /// <c>*</c>, <c>?</c>, quotes, <c>&lt;</c>, <c>&gt;</c>, <c>|</c>), and no leading or trailing
    /// whitespace or trailing dot.
    /// </summary>
    public static bool IsValid(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= 200
        && name.IndexOfAny(_invalid) < 0
        && !name.Contains("..", StringComparison.Ordinal)
        && !name.Any(char.IsControl)
        && !char.IsWhiteSpace(name[0])
        && !char.IsWhiteSpace(name[^1])
        && name[^1] != '.';

    /// <summary>Throws <see cref="ArgumentException"/> when <paramref name="name"/> is not <see cref="IsValid"/>.</summary>
    public static void ThrowIfInvalid(string? name, string what, string paramName)
    {
        if (!IsValid(name))
            throw new ArgumentException(
                $"Invalid {what} '{name}': it is used in a file name, so it must not be empty and must not contain " +
                "path separators, '..', control characters, ':', '*', '?', '\"', '<', '>' or '|', " +
                "or start or end with whitespace or end with '.'.", paramName);
    }
}
