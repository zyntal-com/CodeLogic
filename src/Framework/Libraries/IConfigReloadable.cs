using CodeLogic.Core.Configuration;

namespace CodeLogic.Framework.Libraries;

/// <summary>
/// Optional interface for libraries that can apply config changes while running,
/// without a restart. Implement it next to <see cref="ILibrary"/>.
/// <para>
/// Called after a section of this library changed — via
/// <see cref="LibraryManager.UpdateConfigAsync"/>, <see cref="LibraryManager.ReloadConfigAsync"/>,
/// <see cref="LibraryManager.ResetConfigAsync"/>, the config file watcher, or the library's own
/// <see cref="IConfigurationManager.SaveAsync{T}"/>. Only called once the library is initialized.
/// </para>
/// <para>
/// Use <see cref="ConfigDiff.Keyed{TValue}"/> on the old/new values to touch only the instances
/// that changed (e.g. reconnect one database and leave the others alone).
/// </para>
/// </summary>
public interface IConfigReloadable
{
    /// <summary>
    /// Apply a committed config change. The new values are already persisted and returned by
    /// <c>Get&lt;T&gt;()</c>. Throw to report that the change could not be applied — the file is kept,
    /// the error is returned to the caller, and the library reports Degraded health until a
    /// later change applies successfully.
    /// </summary>
    /// <param name="context">The library context.</param>
    /// <param name="change">Section, kind, and old/new snapshots of the effective config.</param>
    /// <param name="cancellationToken">Cancellation for the triggering operation.</param>
    Task OnConfigChangedAsync(LibraryContext context, ConfigChangedEventArgs change, CancellationToken cancellationToken);
}
