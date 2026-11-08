using System.ComponentModel.DataAnnotations;

namespace CodeLogic.Core.Configuration;

/// <summary>
/// Base class for all CodeLogic configuration models.
/// Inherit from this and use DataAnnotations attributes for validation.
/// Override Validate() for custom validation logic.
/// </summary>
public abstract class ConfigModelBase
{
    /// <summary>
    /// Validates this configuration model using DataAnnotations and any
    /// custom logic in overriding classes.
    /// </summary>
    public virtual ConfigValidationResult Validate()
    {
        var context = new ValidationContext(this);
        var results = new List<ValidationResult>();

        if (Validator.TryValidateObject(this, context, results, validateAllProperties: true))
            return ConfigValidationResult.Valid();

        var errors = results
            .Select(r => r.ErrorMessage ?? "Unknown validation error")
            .ToList();

        // Field-scoped errors use the schema path of the member (top-level properties: camelCase or [JsonPropertyName]).
        var fieldErrors = results
            .SelectMany(r => r.MemberNames.Select(member => new ConfigFieldError(
                FieldPath(member), r.ErrorMessage ?? "Unknown validation error")))
            .ToList();

        return ConfigValidationResult.Invalid(fieldErrors, errors);
    }

    private string FieldPath(string member) =>
        GetType().GetProperty(member) is { } prop
            ? ConfigSchemaBuilder.JsonName(prop)
            : System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(member);
}

/// <summary>
/// Describes a configuration model for schemas and admin UIs (name, description, version).
/// It does not affect the file name: that comes from <c>Register&lt;T&gt;(subConfigName)</c>,
/// e.g. <c>Register&lt;DatabaseConfig&gt;("database")</c> → <c>config.database.json</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ConfigSectionAttribute : Attribute
{
    /// <summary>
    /// The section's display name. Informational; the file name comes from the name passed to
    /// <c>Register&lt;T&gt;</c>.
    /// </summary>
    public string SectionName { get; }

    /// <summary>
    /// Schema version for the config section. Informational — not used for migration.
    /// Default: 1.
    /// </summary>
    public int Version { get; init; } = 1;

    /// <summary>
    /// Optional description of the section, returned as <see cref="ConfigSchema.Description"/> and
    /// <see cref="ConfigSectionInfo.Description"/> for admin UIs.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Initializes the attribute with a section name.
    /// </summary>
    /// <param name="sectionName">The section name (informational; it does not set the file name).</param>
    public ConfigSectionAttribute(string sectionName)
    {
        SectionName = sectionName;
    }
}
