using System.Runtime.Loader;

namespace CodeLogic.Tests.PluginFixture.Dependency;

/// <summary>Reports which load context this dependency was loaded into.</summary>
public static class DependencyMarker
{
    public static string LoadContextName =>
        AssemblyLoadContext.GetLoadContext(typeof(DependencyMarker).Assembly)?.Name ?? "<none>";
}
