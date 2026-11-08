# Localization

CodeLogic provides per-component locale files with culture fallback, format string support, and hot-reload. Each library manages its own strings independently.

---

## LocalizationModelBase

All localization classes inherit from `LocalizationModelBase`:

```csharp
public abstract class LocalizationModelBase
{
    public string Culture { get; set; } = "en-US";
}
```

Define string properties with default values as the fallback (usually English):

```csharp
[LocalizationSection("ui")]
public class UiStrings : LocalizationModelBase
{
    [LocalizedString(Description = "{0} = user name")]
    public string WelcomeMessage { get; set; } = "Welcome, {0}!";

    public string LogoutConfirm { get; set; } = "Are you sure you want to log out?";

    [LocalizedString(Description = "{0} = number of items")]
    public string ItemCount { get; set; } = "You have {0} items.";
}
```

---

## LocalizationSectionAttribute

Sets the file name prefix for a localization model. Without it, the type name is used (`UiStrings.en-US.json`):

```csharp
[LocalizationSection("ui")]
public class UiStrings : LocalizationModelBase { ... }
```

Files are placed in `{LibraryDirectory}/localization/`:

| Attribute | Culture | File |
|-----------|---------|------|
| `[LocalizationSection("ui")]` | `en-US` (default) | `localization/ui.en-US.json` |
| `[LocalizationSection("ui")]` | `da-DK` | `localization/ui.da-DK.json` |
| `[LocalizationSection("ui")]` | `de-DE` | `localization/ui.de-DE.json` |

The cultures come from `localization.supportedCultures` in `CodeLogic.json`; the default from `localization.defaultCulture`.

---

## LocalizedStringAttribute

Documents a string for translators. It has no runtime effect: every public property is written to the locale files, with or without it.

```csharp
[LocalizedString(Description = "Shown after login. {0} = user name")]
public string WelcomeMessage { get; set; } = "Welcome, {0}!";
```

---

## Registering Localization Models

Register in `OnConfigureAsync`:

```csharp
public Task OnConfigureAsync(LibraryContext context)
{
    context.Localization.Register<UiStrings>();
    return Task.CompletedTask;
}
```

---

## Reading Localized Strings

Read in `OnInitializeAsync` or later:

```csharp
private UiStrings _strings = null!;

public Task OnInitializeAsync(LibraryContext context)
{
    _strings = context.Localization.Get<UiStrings>();          // default culture
    return Task.CompletedTask;
}

public string GetWelcome(string username)
    => string.Format(_strings.WelcomeMessage, username);
```

---

## ILocalizationManager API

```csharp
public interface ILocalizationManager
{
    void Register<T>() where T : LocalizationModelBase, new();
    T Get<T>(string? culture = null) where T : LocalizationModelBase, new();
    Task GenerateTemplatesAsync<T>(IReadOnlyList<string> cultures) where T : LocalizationModelBase, new();
    Task LoadAsync<T>(IReadOnlyList<string> cultures, bool generateIfMissing = true) where T : LocalizationModelBase, new();
    Task GenerateAllTemplatesAsync(IReadOnlyList<string> cultures);
    Task LoadAllAsync(IReadOnlyList<string> cultures, bool generateIfMissing = true);
    Task ReloadAllAsync(IReadOnlyList<string> cultures);
    IReadOnlyList<string> GetLoadedCultures<T>() where T : LocalizationModelBase, new();
}
```

The framework generates and loads the files for you after `OnConfigureAsync`. There is no "current culture" on the manager: pass the culture to `Get<T>` per call.

---

## Culture Fallback

`Get<T>("da-DK")` returns the Danish strings when `da-DK` is loaded. Otherwise it returns the default culture. There is no parent-culture step (`da-DK` does not fall back to `da`).

A culture file only needs the strings that differ: missing keys are filled in from the default culture. So you can ship the default culture and add translations incrementally.

---

## Locale File Format

Locale files are plain JSON:

```json
// localization/ui.da-DK.json
{
  "culture": "da-DK",
  "welcomeMessage": "Velkommen, {0}!",
  "logoutConfirm": "Er du sikker på, at du vil logge ud?",
  "itemCount": "Du har {0} elementer."
}
```

Keys are camelCase. Use `{0}`, `{1}`, etc. as placeholders and format with `string.Format`.

---

## Hot Reload

Reload localization files at runtime:

```csharp
var cultures = CodeLogic.GetConfiguration().Localization.SupportedCultures;
await context.Localization.ReloadAllAsync(cultures);
```

Useful when locale files are updated without restarting the application. `Get<T>` keeps returning the old strings until every file has loaded. Afterwards the framework publishes `LocalizationReloadedEvent` with the component's ID.

---

## Per-request culture

Pick the culture per call, for example from the user's profile or the request:

```csharp
var strings = context.Localization.Get<UiStrings>("da-DK");
// Danish strings, or the default culture when da-DK is not loaded
```
