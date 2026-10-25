# CodeLogic 4

[![NuGet](https://img.shields.io/nuget/v/CodeLogic?label=nuget&color=blue)](https://www.nuget.org/packages/CodeLogic)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](https://github.com/zyntal-com/CodeLogic/blob/main/LICENSE)

A modular .NET 10 framework for building structured, lifecycle-managed applications with clean separation between core infrastructure, framework orchestration, and library integrations.

## Why CodeLogic?

Most .NET apps start with `Program.cs` and grow organically — services get registered in random order, configuration is scattered, and adding a plugin system means building one from scratch.

CodeLogic gives you a **structured lifecycle** out of the box:

- **Libraries** (reusable integrations) and **Applications** (your business logic) follow the same 4-phase lifecycle: Configure → Initialize → Start → Stop. Dependencies are always resolved in the right order.
- **Configuration** and **Localization** are built in — not bolted on. Each library and application gets its own config file, auto-generated on first run with defaults and validation. Config can be changed and reloaded while running — libraries can apply a change to just the instance that changed (e.g. one database out of three) without a restart.
- **Plugins** are first-class citizens with hot-reload, isolated assembly contexts, and the same lifecycle as libraries.
- **Zero external dependencies.** CodeLogic itself has no NuGet dependencies — it's pure .NET 10. External integrations (MySQL, S3, SMTP, etc.) are optional library packages in the [CodeLogic.* family](https://github.com/zyntal-com/CodeLogic.Libs).

## Install

```bash
dotnet add package CodeLogic
```

## Quick Start

```csharp
var result = await CodeLogic.InitializeAsync(opts =>
{
    opts.FrameworkRootPath = "data/codelogic";
    opts.AppVersion = "1.0.0";
});
if (result.ShouldExit) return;

// Load optional libraries (each is a separate NuGet package)
await Libraries.LoadAsync<MySQL2Library>();
await Libraries.LoadAsync<MailLibrary>();

// Register your application
CodeLogic.RegisterApplication(new MyApplication());

// Boot: Configure → Initialize → Start (in dependency order)
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

// ... your app runs ...

await CodeLogic.StopAsync();
```

On first run, CodeLogic creates a `data/codelogic/` directory with auto-generated config files for each library and your application. Edit them to configure — no code changes needed.

## Lifecycle

Every library and application follows the same 4-phase lifecycle:

```
InitializeAsync()       Load CodeLogic.json, validate, create LibraryManager
RegisterApplication()   Declare the consuming app
ConfigureAsync()        Run OnConfigureAsync on app + libraries
                        (generates/loads config and localization files)
StartAsync()            Libraries: OnInitializeAsync → OnStartAsync
                        Application: OnInitializeAsync → OnStartAsync

--- application runs ---

StopAsync()             Application OnStopAsync, unload plugins, then stop libraries
```

Libraries start before the application and stop after it — so your app can always rely on its libraries being available.

## Architecture

```
CodeLogic/
├── src/
│   ├── Core/               # Pure infrastructure — no framework coupling
│   │   ├── Logging/        # ILogger with scoped file-based output
│   │   ├── Configuration/  # Auto-generated JSON config with validation
│   │   ├── Localization/   # Culture-aware string management
│   │   └── Utilities/      # SemanticVersion, StartupValidator, FirstRunManager
│   │
│   ├── Framework/          # Lifecycle orchestration — uses Core
│   │   ├── Libraries/      # ILibrary, LibraryContext, LibraryManager
│   │   ├── Application/    # IApplication, ApplicationContext
│   │   └── Plugins/        # IPlugin, PluginContext, PluginManager (hot-reload)
│   │
│   └── CodeLogic.cs        # Static facade + ICodeLogicRuntime (injectable)
│
├── docs/                   # Documentation (getting started, articles, API reference)
└── samples/                # Example applications
```

### Key Design Principles

**Core is standalone.** Logging, Configuration, and Localization have zero dependencies on the framework lifecycle. You can use them in any .NET app without the rest of CodeLogic.

**Framework wires Core into lifecycle.** `LibraryContext` and `ApplicationContext` bundle Core engines and hand them to libraries/apps at the right phase — nothing gets constructed until it's needed.

**Static facade, injectable runtime.** The `CodeLogic` static class is a convenience facade. For testing or advanced scenarios, inject `ICodeLogicRuntime` via DI.

**Plugins are first-class.** Same lifecycle, same access to config/logging/localization, hot-loadable at runtime via `AssemblyLoadContext` isolation.

## Building a Library

Libraries are reusable integrations that plug into the CodeLogic lifecycle:

```csharp
public class MyLibrary : ILibrary
{
    public LibraryManifest Manifest { get; } = new()
    {
        Id = "my.library", Name = "My Library", Version = "1.0.0"
    };

    public Task OnConfigureAsync(LibraryContext context)
    {
        context.Configuration.Register<MyLibConfig>();
        return Task.CompletedTask;
    }

    public Task OnInitializeAsync(LibraryContext context)
    {
        var config = context.Configuration.Get<MyLibConfig>();
        // Initialize services using validated config
        return Task.CompletedTask;
    }

    public Task OnStartAsync(LibraryContext context) => Task.CompletedTask;
    public Task OnStopAsync() => Task.CompletedTask;
    public Task<HealthStatus> HealthCheckAsync() => Task.FromResult(HealthStatus.Healthy("OK"));
    public void Dispose() { }
}
```

## Building an Application

```csharp
public class MyApplication : IApplication
{
    public ApplicationManifest Manifest { get; } = new()
    {
        Id = "myapp", Name = "My App", Version = "1.0.0"
    };

    public Task OnConfigureAsync(ApplicationContext context)
    {
        context.Configuration.Register<AppSettings>();
        return Task.CompletedTask;
    }

    public Task OnInitializeAsync(ApplicationContext context)
    {
        var settings = context.Configuration.Get<AppSettings>();
        return Task.CompletedTask;
    }

    public Task OnStartAsync(ApplicationContext context) => Task.CompletedTask;
    public Task OnStopAsync() => Task.CompletedTask;
}
```

## Building a Plugin

Plugins are hot-loadable at runtime — no restart required:

```csharp
public class MyPlugin : IPlugin
{
    public string Id => "my.plugin";
    public string Name => "My Plugin";
    public string Version => "1.0.0";
    public string? Description => "Does something cool";
    public string? Author => "You";
    public bool IsLoaded { get; private set; }

    public async Task OnLoadAsync(PluginContext context)
    {
        var config = context.Configuration.Get<MyPluginConfig>();
        IsLoaded = true;
    }

    public Task OnUnloadAsync() { IsLoaded = false; return Task.CompletedTask; }
    public Task<HealthStatus> HealthCheckAsync() => Task.FromResult(HealthStatus.Healthy("OK"));
    public void Dispose() { }
}
```

## Official Library Packages

The [CodeLogic.* library family](https://github.com/zyntal-com/CodeLogic.Libs) provides production-ready integrations:

| Package | Description |
|---------|-------------|
| [CodeLogic.Common](https://www.nuget.org/packages/CodeLogic.Common) | Shared utilities — hashing, caching, imaging, compression |
| [CodeLogic.MySQL2](https://www.nuget.org/packages/CodeLogic.MySQL2) | MySQL with LINQ query builder, table sync, migrations |
| [CodeLogic.SQLite](https://www.nuget.org/packages/CodeLogic.SQLite) | SQLite with connection pooling and LINQ queries |
| [CodeLogic.PostgreSQL](https://www.nuget.org/packages/CodeLogic.PostgreSQL) | PostgreSQL integration |
| [CodeLogic.Mail](https://www.nuget.org/packages/CodeLogic.Mail) | SMTP/IMAP email with template engine |
| [CodeLogic.Storage](https://www.nuget.org/packages/CodeLogic.Storage) | Provider-neutral mounted storage for local filesystems, S3/R2/MinIO, FTP/FTPS, SFTP, WebDAV, Azure Blob, Google Cloud Storage, and OpenStack Swift |
| [CodeLogic.SocialConnect](https://www.nuget.org/packages/CodeLogic.SocialConnect) | Discord webhooks + Steam Web API |
| [CodeLogic.NetUtils](https://www.nuget.org/packages/CodeLogic.NetUtils) | DNS, DNSBL, IP geolocation |
| [CodeLogic.GameNetQuery](https://www.nuget.org/packages/CodeLogic.GameNetQuery) | Game server queries (Valve RCON, Source UDP, Minecraft) |
| [CodeLogic.SystemStats](https://www.nuget.org/packages/CodeLogic.SystemStats) | Cross-platform CPU/memory/process monitoring |
| [CodeLogic.GitHelper](https://www.nuget.org/packages/CodeLogic.GitHelper) | Git repository management |
| [CodeLogic.TwoFactorAuth](https://www.nuget.org/packages/CodeLogic.TwoFactorAuth) | TOTP 2FA + QR code generation |

Each library follows the same lifecycle pattern — load it with `Libraries.LoadAsync<T>()`, configure it via its auto-generated JSON config file, and use it.

For storage, see the [complete `CL.Storage` guide](https://zyntal-com.github.io/CodeLogic.Libs/libs/storage/index.html)
for named multi-provider connections, configuration, transfers, metadata, tags, versions, signed URLs,
health checks, and native-provider access. Existing `CodeLogic.StorageS3` users can follow the
[migration guide](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/CL.Storage/MIGRATION.md).

## Requirements

- .NET 10 SDK or later
- No external NuGet dependencies (the core framework is self-contained)

## Documentation

- [Getting Started](https://github.com/zyntal-com/CodeLogic/blob/main/docs/getting-started.md)
- [Application Lifecycle](https://github.com/zyntal-com/CodeLogic/blob/main/docs/articles/application-lifecycle.md)
- [Library Lifecycle](https://github.com/zyntal-com/CodeLogic/blob/main/docs/articles/library-lifecycle.md)
- [Plugins](https://github.com/zyntal-com/CodeLogic/blob/main/docs/articles/plugins.md)
- [Configuration](https://github.com/zyntal-com/CodeLogic/blob/main/docs/articles/configuration.md)
- [Localization](https://github.com/zyntal-com/CodeLogic/blob/main/docs/articles/localization.md)
- [Event Bus](https://github.com/zyntal-com/CodeLogic/blob/main/docs/articles/event-bus.md)
- [Health Checks](https://github.com/zyntal-com/CodeLogic/blob/main/docs/articles/health-checks.md)
- [CLI Arguments](https://github.com/zyntal-com/CodeLogic/blob/main/docs/Reference/cli-args.md)

## License

MIT — see [LICENSE](https://github.com/zyntal-com/CodeLogic/blob/main/LICENSE)
