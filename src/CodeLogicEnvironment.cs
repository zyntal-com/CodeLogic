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
    /// The environment name from the first set variable of <c>CODELOGIC_ENVIRONMENT</c>,
    /// <c>DOTNET_ENVIRONMENT</c> and <c>ASPNETCORE_ENVIRONMENT</c>, or <see langword="null"/> when none is set.
    /// </summary>
    public static string? EnvironmentName =>
        FirstSet("CODELOGIC_ENVIRONMENT", "DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT");

    /// <summary>
    /// True when <see cref="EnvironmentName"/> is <c>Development</c> (case-insensitive).
    /// Unless <see cref="CodeLogicOptions.UseDevelopmentConfig"/> overrides it, this selects the framework
    /// config file: <c>CodeLogic.Development.json</c> when true (and the file exists), <c>CodeLogic.json</c>
    /// otherwise. An attached debugger or a DEBUG build no longer counts as development.
    /// </summary>
    public static bool IsDevelopment =>
        string.Equals(EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase);

    private static string? FirstSet(params ReadOnlySpan<string> names)
    {
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
    }
}
