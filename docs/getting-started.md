# Getting Started with CodeLogic 4

CodeLogic is a modular .NET 10 framework for building structured applications with libraries, plugins, configuration, localization, events, and health checks. This guide takes you from zero to a running application.

---

## Installation

Add CodeLogic as a project reference:

```xml
<ItemGroup>
  <ProjectReference Include="path/to/CodeLogic/src/CodeLogic.csproj" />
</ItemGroup>
```

Your project should target .NET 10:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
```

---

## The 5-Step Boot Sequence

```csharp
using CodeLogic;

var result = await CodeLogic.InitializeAsync(o =>
{
    o.AppVersion = "1.0.0";
    o.FrameworkRootPath = "CodeLogic";
});

if (result.ShouldExit) return;

await Libraries.LoadAsync<MyLibrary>();
CodeLogic.RegisterApplication(new MyApp());

await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

if (result.RunHealthCheck)
{
    var report = await CodeLogic.GetHealthAsync();
    Console.WriteLine(report.ToConsoleString());
    await CodeLogic.StopAsync();
    return;
}

await Task.Delay(Timeout.Infinite);
```

### What each step does

| Step | Method | What happens |
|------|--------|--------------|
| 1 | `InitializeAsync` | Parses CLI args, scaffolds the directory tree on first run, loads framework config, creates `LibraryManager` |
| 2 | `Libraries.LoadAsync<T>` | Registers library types before configure/start |
| 3 | `RegisterApplication` | Registers your `IApplication` implementation |
| 4 | `ConfigureAsync` | Discovers DLL libraries, runs `OnConfigureAsync` on the application and libraries, then generates or loads config/localization files |
| 5 | `StartAsync` | Runs `OnInitializeAsync` then `OnStartAsync` on libraries first, then the application |

---

## First Run Behavior

On first run, CodeLogic scaffolds the directory tree and continues startup normally. Missing config files are generated with defaults as part of `ConfigureAsync()`.

Typical layout:

```text
YourApp.exe
CodeLogic/
  Framework/
    CodeLogic.json
    CodeLogic.Development.json
    logs/
      framework.log
  Libraries/
    CL.YourLibrary/
      config.json
      localization/
        strings.en-US.json
      logs/
      data/
  Application/
    config.json
    localization/
    logs/
    data/
  Plugins/
```

---

## Development vs Production Config

The runtime chooses one complete framework config file:

| Condition | File loaded |
|-----------|-------------|
| `DEBUG` build or debugger attached | `CodeLogic.Development.json` |
| Release build without debugger | `CodeLogic.json` |

`CodeLogic.Development.json` is a full replacement file, not an overlay or merge. If a section is omitted there, it falls back to the model's default values, not to values from `CodeLogic.json`.

Example development file:

```json
{
  "framework": {
    "name": "CodeLogic",
    "version": "4.8.142"
  },
  "logging": {
    "mode": "singleFile",
    "maxFileSizeMb": 10,
    "maxRolledFiles": 5,
    "fileNamePattern": "{date:yyyy}/{date:MM}/{date:dd}/{level}.log",
    "globalLevel": "Debug",
    "enableConsoleOutput": true,
    "consoleMinimumLevel": "Debug",
    "enableDebugMode": true,
    "centralizedDebugLog": false,
    "centralizedLogsPath": null,
    "includeMachineName": true,
    "timestampFormat": "yyyy-MM-dd HH:mm:ss.fff"
  },
  "localization": {
    "defaultCulture": "en-US",
    "supportedCultures": ["en-US", "da-DK"],
    "autoGenerateTemplates": true
  },
  "libraries": {
    "discoveryPattern": "CL.*",
    "enableDependencyResolution": true
  },
  "healthChecks": {
    "enabled": true,
    "intervalSeconds": 10
  }
}
```

Add `CodeLogic.Development.json` to `.gitignore`.

---

## Your First Application

```csharp
using CodeLogic.Core.Configuration;
using CodeLogic.Framework.Application;

public class MyApp : IApplication
{
    public ApplicationManifest Manifest => new()
    {
        Id = "myapp",
        Name = "My Application",
        Version = "1.0.0"
    };

    public Task OnConfigureAsync(ApplicationContext context)
    {
        context.Configuration.Register<AppConfig>();
        return Task.CompletedTask;
    }

    public Task OnInitializeAsync(ApplicationContext context)
    {
        var config = context.Configuration.Get<AppConfig>();
        context.Logger.Info($"GreetingName={config.GreetingName}");
        return Task.CompletedTask;
    }

    public Task OnStartAsync(ApplicationContext context) => Task.CompletedTask;
    public Task OnStopAsync() => Task.CompletedTask;
    public Task<HealthStatus> HealthCheckAsync() => Task.FromResult(HealthStatus.Healthy("Running"));
}

public class AppConfig : ConfigModelBase
{
    public string GreetingName { get; set; } = "World";
}
```

---

## Your First Library

```csharp
using CodeLogic.Core.Configuration;
using CodeLogic.Framework.Libraries;

public class MyLibrary : ILibrary
{
    public LibraryManifest Manifest => new()
    {
        Id = "CL.MyLibrary",
        Name = "My Library",
        Version = "1.0.0"
    };

    public Task OnConfigureAsync(LibraryContext context)
    {
        context.Configuration.Register<MyLibraryConfig>();
        return Task.CompletedTask;
    }

    public Task OnInitializeAsync(LibraryContext context)
    {
        var config = context.Configuration.Get<MyLibraryConfig>();
        context.Logger.Info($"MaxItems={config.MaxItems}");
        return Task.CompletedTask;
    }

    public Task OnStartAsync(LibraryContext context) => Task.CompletedTask;
    public Task OnStopAsync() => Task.CompletedTask;
    public Task<HealthStatus> HealthCheckAsync() => Task.FromResult(HealthStatus.Healthy("All good"));
    public void Dispose() { }
}

public class MyLibraryConfig : ConfigModelBase
{
    public int MaxItems { get; set; } = 100;
}
```

---

## Useful CLI Flags

| Flag | Description |
|------|-------------|
| `--version` | Print version and exit |
| `--info` | Print framework info and exit |
| `--health` | Run a health check after startup |
| `--generate-configs` | Generate missing config files before startup continues |
| `--generate-configs-force` | Overwrite existing config files with defaults |
| `--dry-run` | Run `ConfigureAsync()` only, validate startup inputs, and do not write files |

---

## Minimal Working Example

```csharp
using CodeLogic;

var result = await CodeLogic.InitializeAsync(o => o.AppVersion = "1.0.0");
if (result.ShouldExit) return;

await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

Console.WriteLine("Running. Press Ctrl+C to stop.");
await Task.Delay(Timeout.Infinite);
```

No libraries and no application are required to boot the framework itself. Add libraries with `Libraries.LoadAsync<T>()` and register an application with `CodeLogic.RegisterApplication(new MyApp())` when you are ready.
