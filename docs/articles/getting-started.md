# Getting Started

This guide walks you through installing CodeLogic 5, understanding the boot flow, and writing your first library and application.

---

## Installation

```xml
<ItemGroup>
  <ProjectReference Include="path/to/CodeLogic/src/CodeLogic.csproj" />
</ItemGroup>
```

Target `net11.0` in your host project.

---

## Boot Sequence

```csharp
using CodeLogic;

var result = await CodeLogic.InitializeAsync(o =>
{
    o.AppVersion = "1.0.0";
    o.FrameworkRootPath = "CodeLogic";
});

if (result.ShouldExit) return;

await Libraries.LoadAsync<MyDatabaseLibrary>();
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

| Step | Method | What happens |
|------|--------|--------------|
| 1 | `InitializeAsync` | Loads framework config and creates the runtime managers |
| 2 | `Libraries.LoadAsync<T>` | Registers libraries before configure/start |
| 3 | `RegisterApplication` | Registers the consuming application |
| 4 | `ConfigureAsync` | Runs `OnConfigureAsync` on the application and libraries, then generates or loads config/localization files |
| 5 | `StartAsync` | Initializes and starts libraries first, then the application |

---

## First Run

On first run, CodeLogic scaffolds the folder structure and keeps starting. Missing config files are generated automatically during `ConfigureAsync()`.

Use `--generate-configs-force` to overwrite existing config files with defaults.

---

## Development Config

`CodeLogic.Development.json` is loaded instead of `CodeLogic.json` when running in a `DEBUG` build or with a debugger attached.

It is a full replacement file, not an overlay. Keep the full schema in that file and add it to `.gitignore`.

---

## Your First Library

```csharp
using CodeLogic.Core.Configuration;
using CodeLogic.Framework.Libraries;

public class MyDatabaseLibrary : ILibrary
{
    public LibraryManifest Manifest => new()
    {
        Id = "CL.MyDatabase",
        Name = "Database Library",
        Version = "1.0.0"
    };

    public Task OnConfigureAsync(LibraryContext context)
    {
        context.Configuration.Register<MyDbConfig>();
        return Task.CompletedTask;
    }

    public Task OnInitializeAsync(LibraryContext context)
    {
        var config = context.Configuration.Get<MyDbConfig>();
        context.Logger.Info($"Connection string length={config.ConnectionString.Length}");
        return Task.CompletedTask;
    }

    public Task OnStartAsync(LibraryContext context) => Task.CompletedTask;
    public Task OnStopAsync() => Task.CompletedTask;
    public Task<HealthStatus> HealthCheckAsync() => Task.FromResult(HealthStatus.Healthy("Connected"));
    public void Dispose() { }
}
```

---

## Your First Application

```csharp
using CodeLogic.Core.Configuration;
using CodeLogic.Framework.Application;

public class MyApp : IApplication
{
    public ApplicationManifest Manifest => new()
    {
        Id = "MyApp",
        Name = "My Application",
        Version = "1.0.0"
    };

    public Task OnConfigureAsync(ApplicationContext context)
    {
        context.Configuration.Register<MyAppConfig>();
        return Task.CompletedTask;
    }

    public Task OnInitializeAsync(ApplicationContext context)
    {
        var config = context.Configuration.Get<MyAppConfig>();
        context.Logger.Info($"Configured name={config.Name}");
        return Task.CompletedTask;
    }

    public Task OnStartAsync(ApplicationContext context) => Task.CompletedTask;
    public Task OnStopAsync() => Task.CompletedTask;
    public Task<HealthStatus> HealthCheckAsync() => Task.FromResult(HealthStatus.Healthy("Running"));
}
```

---

## CLI Flags

| Flag | Description |
|------|-------------|
| `--version` | Print version and exit |
| `--info` | Print runtime info and exit |
| `--health` | Run a health check after startup |
| `--generate-configs` | Generate missing config files before startup continues |
| `--generate-configs-force` | Overwrite existing config files with defaults |
| `--dry-run` | Run configure only, validate startup inputs, and do not write files |

---

## Next Steps

- [Library Lifecycle](library-lifecycle.md)
- [Configuration](configuration.md)
- [Localization](localization.md)
- [Event Bus](event-bus.md)
- [Health Checks](health-checks.md)
- [Plugins](plugins.md)
