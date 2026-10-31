namespace CodeLogic.Core.Utilities;

/// <summary>
/// File-system path comparison that matches the platform: case-insensitive on Windows and macOS
/// (their default file systems), case-sensitive everywhere else.
/// </summary>
internal static class PathComparison
{
    /// <summary>True when the platform's default file system treats paths case-insensitively.</summary>
    public static bool IgnoreCase { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>The comparer to key dictionaries and sets of paths with.</summary>
    public static StringComparer Comparer { get; } = IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
