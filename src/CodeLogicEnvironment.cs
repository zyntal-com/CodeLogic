using System.Diagnostics;
using System.Reflection;

namespace CodeLogic;

/// <summary>
/// Provides read-only runtime environment information.
/// AppVersion is set by the consuming application via CodeLogicOptions.AppVersion.
/// </summary>
public static class CodeLogicEnvironment
{
    /// <summary>The machine name from the operating system.</summary>
    public static string MachineName => Environment.MachineName;

    /// <summary>The application base directory (where the executable lives).</summary>
    public static string AppRootPath => AppContext.BaseDirectory;

    /// <summary>
    /// The application version. Set automatically from CodeLogicOptions.AppVersion
    /// during InitializeAsync. Defaults to "0.0.0".
    /// </summary>
    public static string AppVersion { get; internal set; } = "0.0.0";

    /// <summary>
    /// The version of the CodeLogic framework assembly, from its informational version without the
    /// <c>+commit</c> build metadata (e.g. <c>5.0.42</c> or <c>5.0.42-preview</c>). Falls back to the
    /// assembly version when the attribute is missing.
    /// </summary>
    public static string FrameworkVersion { get; } = ReadFrameworkVersion();

    private static string ReadFrameworkVersion()
    {
        var assembly = typeof(CodeLogicEnvironment).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }
        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>
    /// Returns true if a debugger is currently attached to this process.
    /// </summary>
    public static bool IsDebugging => Debugger.IsAttached;

    /// <summary>
    /// Returns true when running in development mode.
    /// True if debugger is attached OR if this is a DEBUG build.
    /// Mirrors the framework's config file selection logic:
    ///   IsDevelopment = true  → CodeLogic.Development.json
    ///   IsDevelopment = false → CodeLogic.json
    /// </summary>
    public static bool IsDevelopment
    {
        get
        {
            if (Debugger.IsAttached) return true;
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }
}
