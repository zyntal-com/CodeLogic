---
_layout: landing
---

# CodeLogic 4

CodeLogic 4 is a .NET 11 framework for lifecycle-managed applications with libraries, plugins, configuration, localization, events, and health checks.

---

## Three Layers

- `IApplication` for your host application
- `ILibrary` for reusable services that start before the app
- `IPlugin` for optional app-managed extensions

All three use the same four phases:

| Phase | Method | Purpose |
|-------|--------|---------|
| Configure | `OnConfigureAsync` | Register config and localization models |
| Initialize | `OnInitializeAsync` | Read config and prepare services |
| Start | `OnStartAsync` | Begin processing and background work |
| Stop | `OnStopAsync` | Shut down cleanly |

---

## Startup Order

```text
ConfigureAsync()
  application.OnConfigureAsync()
  libraries.OnConfigureAsync()
  generate/load config and localization files

StartAsync()
  libraries.OnInitializeAsync()
  libraries.OnStartAsync()
  application.OnInitializeAsync()
  application.OnStartAsync()

StopAsync()
  application.OnStopAsync()
  plugins.OnUnloadAsync()
  libraries.OnStopAsync()
```

Libraries are initialized and started before the application, so application startup can safely depend on library services already being ready.

---

## Quick Start

```csharp
using CodeLogic;

var result = await CodeLogic.InitializeAsync(o =>
{
    o.AppVersion = "1.0.0";
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

---

## Key Features

| Feature | Description |
|---------|-------------|
| Application lifecycle | Your app participates in the same lifecycle as libraries |
| Library system | Reusable services with manifests, dependencies, and health checks |
| Plugin system | Optional app-managed plugins with runtime loading support |
| Configuration | Per-component JSON config with generation and validation |
| Localization | Per-component localization files with culture fallback |
| Event bus | Shared event bus across libraries, app, and plugins |
| Health checks | Aggregate reports plus scheduled per-component events |
| CLI flags | `--version`, `--info`, `--health`, `--generate-configs`, `--generate-configs-force`, `--dry-run` |

---

## Official Libraries

Production-ready integrations are maintained in the
[CodeLogic.Libs repository](https://github.com/zyntal-com/CodeLogic.Libs) and follow the same lifecycle.

[`CodeLogic.Storage`](https://zyntal-com.github.io/CodeLogic.Libs/libs/storage/index.html) provides one
provider-neutral API over named local, S3-compatible, FTP/FTPS, SFTP, WebDAV, Azure Blob, Google
Cloud Storage, and OpenStack Swift connections. It supports cross-provider transfers, capabilities,
metadata, tags, versions, signed URLs, health checks, and native-provider escape hatches.

---

## Directory Layout

```text
CodeLogic/
  Framework/
    CodeLogic.json
    CodeLogic.Development.json
    logs/
  Application/
    config.json
    localization/
    logs/
    data/
  Libraries/
    CL.MyLibrary/
      config.json
      localization/
      logs/
      data/
  Plugins/
```

---

## Next Steps

- [Getting Started](articles/getting-started.md)
- [Configuration](articles/configuration.md)
- [Event Bus](articles/event-bus.md)
- [Plugins](articles/plugins.md)
- [API Reference](api/index.md)
