using System.Reflection;
using System.Runtime.Loader;

namespace CodeLogic.Framework.Application.Plugins;

/// <summary>
/// Collectible <see cref="AssemblyLoadContext"/> for one plugin.
/// </summary>
/// <remarks>
/// Resolution order for a dependency:
/// <list type="number">
/// <item><b>Host contracts</b> (<c>CodeLogic</c> and <see cref="PluginOptions.SharedAssemblies"/>) always
/// resolve to the host's copy, so types that cross the host/plugin boundary have one identity.</item>
/// <item><b>Plugin dependencies</b> listed in the plugin's <c>.deps.json</c> or present in its folder
/// load into this context, isolated from the host and from other plugins.</item>
/// <item>Anything else (the .NET runtime, assemblies only the host ships) falls back to the default
/// context.</item>
/// </list>
/// Assemblies loaded by another plugin's context are never returned, so one plugin cannot receive an
/// incompatible version from another or keep it from unloading. Isolation is about assembly
/// resolution only: plugins run in the host process with its permissions, statics of shared
/// assemblies and the shared event bus.
/// </remarks>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly string HostAssemblyName = typeof(PluginLoadContext).Assembly.GetName().Name!;

    private readonly AssemblyDependencyResolver _resolver;
    private readonly HashSet<string> _shared;

    /// <summary>Creates a new plugin load context for the assembly at the specified path.</summary>
    /// <param name="pluginPath">The plugin's main assembly.</param>
    /// <param name="sharedAssemblies">Extra simple names to share with the host; <c>CodeLogic</c> is always shared.</param>
    public PluginLoadContext(string pluginPath, IEnumerable<string>? sharedAssemblies = null)
        : base(name: Path.GetFileNameWithoutExtension(pluginPath), isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(Path.GetFullPath(pluginPath));
        _shared = new HashSet<string>(sharedAssemblies ?? [], StringComparer.OrdinalIgnoreCase) { HostAssemblyName };
    }

    /// <summary>True when <paramref name="assemblyName"/> resolves to the host's copy instead of the plugin's.</summary>
    public bool IsShared(AssemblyName assemblyName)
    {
        ArgumentNullException.ThrowIfNull(assemblyName);
        return assemblyName.Name is { } name && _shared.Contains(name);
    }

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // null defers to the default (host) context.
        if (IsShared(assemblyName))
            return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }

    /// <inheritdoc />
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
