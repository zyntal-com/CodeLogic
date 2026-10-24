namespace CodeLogic.Core.Configuration;

/// <summary>
/// Outcome of a runtime config change: whether the new values were valid (and persisted),
/// and whether the owning component managed to apply them.
/// </summary>
public sealed class ConfigApplyResult
{
    /// <summary>Validation result. When invalid, nothing was persisted or applied.</summary>
    public required ConfigValidationResult Validation { get; init; }

    /// <summary>
    /// Set when the config was valid and persisted, but the component failed to apply it
    /// (e.g. a library could not connect with the new settings).
    /// </summary>
    public Exception? ApplyError { get; init; }

    /// <summary>True when the new values passed validation.</summary>
    public bool IsValid => Validation.IsValid;

    /// <summary>Validation errors (empty when valid).</summary>
    public IReadOnlyList<string> Errors => Validation.Errors;

    /// <summary>True when the change was valid, persisted, and applied without error.</summary>
    public bool Applied => IsValid && ApplyError is null;

    /// <summary>Creates a successful result.</summary>
    public static ConfigApplyResult Success() => new() { Validation = ConfigValidationResult.Valid() };

    /// <summary>Creates a result for a change that failed validation.</summary>
    public static ConfigApplyResult Invalid(ConfigValidationResult validation) => new() { Validation = validation };

    /// <summary>Creates a result for a valid, persisted change that failed to apply.</summary>
    public static ConfigApplyResult Failed(Exception error) =>
        new() { Validation = ConfigValidationResult.Valid(), ApplyError = error };

    /// <inheritdoc />
    public override string ToString() =>
        !IsValid ? Validation.ToString()
        : ApplyError is not null ? $"Saved, but not applied: {ApplyError.Message}"
        : "Applied";
}

/// <summary>
/// Thrown when a config change was valid and persisted, but a change handler
/// (e.g. a library's <c>OnConfigChangedAsync</c>) failed to apply it.
/// The file and in-memory values already contain the new config.
/// </summary>
public sealed class ConfigApplyException : Exception
{
    /// <summary>Initializes the exception for the given section.</summary>
    public ConfigApplyException(string sectionName, Exception inner)
        : base($"Configuration section '{sectionName}' was saved but could not be applied: {inner.Message}", inner)
    {
        SectionName = sectionName;
    }

    /// <summary>The section whose change could not be applied.</summary>
    public string SectionName { get; }
}
