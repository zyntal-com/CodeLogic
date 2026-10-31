# Localization

CodeLogic's localization system provides per-component, per-culture JSON string files with template auto-generation, culture fallback, and live-reload support.

---

## LocalizationModelBase

All localization classes inherit from `LocalizationModelBase`:

```csharp
public abstract class LocalizationModelBase
{
    public string Culture { get; set; } = "en-US";
}
```

Define string properties with English default values. The framework serializes these to JSON files for each supported culture:

```csharp
public class AppStrings : LocalizationModelBase
{
    public string WelcomeMessage { get; set; } = "Welcome to {0}!";
    public string ErrorOccurred { get; set; } = "An error occurred: {0}";
    public string UserNotFound { get; set; } = "User '{0}' was not found.";
}
```

---

## LocalizationSectionAttribute

Controls the file name prefix for a localization model:

```csharp
[LocalizationSection("app")]
public class AppStrings : LocalizationModelBase { ... }
```

File naming rules:

| Attribute | File name (en-US) |
|-----------|-------------------|
| No attribute (uses type name) | `AppStrings.en-US.json` |
| `[LocalizationSection("app")]` | `app.en-US.json` |

---

## LocalizedStringAttribute

Documents a string property for translators. Has no runtime effect — purely informational:

```csharp
public class AppStrings : LocalizationModelBase
{
    [LocalizedString(Description = "Shown on the home screen. {0} = application name")]
    public string WelcomeMessage { get; set; } = "Welcome to {0}!";

    [LocalizedString(Description = "Error placeholder. {0} = error message")]
    public string ErrorOccurred { get; set; } = "An error occurred: {0}";
}
```

---

## ILocalizationManager

Each component gets its own `ILocalizationManager` scoped to its localization directory.

```csharp
public interface ILocalizationManager
{
    void Register<T>() where T : LocalizationModelBase, new();
    T Get<T>(string? culture = null) where T : LocalizationModelBase, new();
    Task GenerateTemplatesAsync<T>(IReadOnlyList<string> cultures) where T : LocalizationModelBase, new();
    Task LoadAsync<T>(IReadOnlyList<string> cultures) where T : LocalizationModelBase, new();
    Task GenerateAllTemplatesAsync(IReadOnlyList<string> cultures);
    Task LoadAllAsync(IReadOnlyList<string> cultures);
    Task ReloadAllAsync(IReadOnlyList<string> cultures);
    IReadOnlyList<string> GetLoadedCultures<T>() where T : LocalizationModelBase, new();
}
```

### Method reference

| Method | Description |
|--------|-------------|
| `Register<T>()` | Registers a localization model type. Call in `OnConfigureAsync`. |
| `Get<T>(culture)` | Returns the instance for the given culture. Falls back to default culture if not available. |
| `GenerateTemplatesAsync<T>(cultures)` | Generates template JSON files for each culture if they don't exist. |
| `LoadAsync<T>(cultures)` | Loads localization files for each culture. Non-default cultures merge with the default. |
| `GenerateAllTemplatesAsync(cultures)` | Generates templates for all registered types and all cultures. |
| `LoadAllAsync(cultures)` | Loads all registered types for all cultures. Called automatically by the framework. |
| `ReloadAllAsync(cultures)` | Reloads all localizations from disk into a new snapshot. `Get<T>` keeps returning the previous translations until every file has loaded, and still does if the reload throws. |
| `GetLoadedCultures<T>()` | Returns the list of cultures that have been successfully loaded for a type. |

Culture names become part of the file name (`{section}.{culture}.json`), so every culture passed to these
methods, and the default culture, must contain only ASCII letters, digits, `-` and `_` (for example `en-US`,
`zh-Hans-CN`). Anything else, such as `../x`, throws `ArgumentException` before a file is touched.

---

## Culture Fallback Behavior

When `Get<T>("da-DK")` is called and `da-DK` is not loaded (or missing some keys), the system falls back to the default culture:

```
Requested: da-DK → Found: da-DK.json → Use it (merged with en-US for missing keys)
Requested: da-DK → Not found → Fall back to en-US
```

The default culture is configured in `CodeLogic.json`:

```json
{
  "localization": {
    "defaultCulture": "en-US",
    "supportedCultures": ["en-US", "da-DK", "de-DE"]
  }
}
```

The default culture's file is always required to be complete. Non-default culture files only need to contain strings that differ from the default.

---

## File Naming and Structure

Given a model `AppStrings` with `[LocalizationSection("app")]` and cultures `["en-US", "da-DK"]`:

```
localization/
  app.en-US.json    ← default culture (complete)
  app.da-DK.json    ← Danish (only overrides that differ from en-US)
```

### Generated template (app.en-US.json)

```json
{
  "culture": "en-US",
  "welcomeMessage": "Welcome to {0}!",
  "errorOccurred": "An error occurred: {0}",
  "userNotFound": "User '{0}' was not found."
}
```

### Danish override (app.da-DK.json) — only different strings

```json
{
  "culture": "da-DK",
  "welcomeMessage": "Velkommen til {0}!",
  "userNotFound": "Brugeren '{0}' blev ikke fundet."
}
```

`errorOccurred` is missing from the Danish file — the system merges from `en-US`.

---

## Reload Support

Localization files can be reloaded at runtime without a restart. This is always safe because strings are pure data:

```csharp
await context.Localization.ReloadAllAsync(supportedCultures);
```

Subscribe to `LocalizationReloadedEvent` to react:

```csharp
context.Events.Subscribe<LocalizationReloadedEvent>(e =>
{
    if (e.ComponentId == "CL.MyLibrary")
    {
        // Refresh any cached string references
        _strings = context.Localization.Get<AppStrings>(currentCulture);
    }
});
```

---

## Code Examples

### Full library localization setup

```csharp
[LocalizationSection("mylib")]
public class MyLibStrings : LocalizationModelBase
{
    [LocalizedString(Description = "Device connected message. {0} = device name")]
    public string DeviceConnected { get; set; } = "Device '{0}' connected.";

    [LocalizedString(Description = "Error connecting. {0} = device name, {1} = error")]
    public string DeviceError { get; set; } = "Device '{0}' error: {1}";
}

public class MyLibrary : ILibrary
{
    public Task OnConfigureAsync(LibraryContext context)
    {
        context.Localization.Register<MyLibStrings>();
        return Task.CompletedTask;
    }

    public Task OnInitializeAsync(LibraryContext context)
    {
        // Get strings for the default culture
        var strings = context.Localization.Get<MyLibStrings>();
        context.Logger.Info(string.Format(strings.DeviceConnected, "Device A"));
        return Task.CompletedTask;
    }
    // ...
}
```

### Culture-aware string resolution

```csharp
// Get the user's preferred culture from config or request
string userCulture = "da-DK";

var strings = context.Localization.Get<MyLibStrings>(userCulture);
// If da-DK is loaded: use it. If not: falls back to defaultCulture.

string message = string.Format(strings.DeviceConnected, deviceName);
```

### Checking which cultures are loaded

```csharp
var loaded = context.Localization.GetLoadedCultures<MyLibStrings>();
// ["en-US", "da-DK"]
```
