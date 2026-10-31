# API Reference

This section contains the complete API reference for CodeLogic 5, generated from XML documentation comments in the source code.

## Namespaces

| Namespace | Description |
|-----------|-------------|
| `CodeLogic` | Entry point (`CodeLogic` static class), `Libraries`, `CodeLogicEnvironment` |
| `CodeLogic.Core` | Core abstractions: `IEventBus`, `ILogger`, `IConfigurationManager`, `ILocalizationManager`, `Result<T>`, `Error` |
| `CodeLogic.Framework` | Library, application, and plugin contracts: `ILibrary`, `IApplication`, `IPlugin`, manifests, contexts |
| `CodeLogic.Runtime` | Internal runtime — boot sequence, `LibraryManager`, `PluginManager` |

## Key Types

### Entry Point
- `CodeLogic` — static class with `InitializeAsync`, `ConfigureAsync`, `StartAsync`, `StopAsync`, `GetHealthAsync`
- `Libraries` — `LoadAsync<T>()` to register libraries

### Library Development
- `ILibrary` — implement this for a library
- `LibraryContext` — provided to each lifecycle phase
- `LibraryManifest` — describes a library's identity and dependencies

### Application Development
- `IApplication` — implement this for the host application
- `ApplicationContext` — provided to the application lifecycle

### Plugin Development
- `IPlugin` — implement this for a hot-reloadable plugin
- `PluginContext` — provided to each plugin lifecycle phase
- `PluginManager` — manages plugin load/unload/reload

### Core Abstractions
- `IEventBus` — publish/subscribe event bus
- `IEvent` — marker interface for events
- `ILogger` — structured logger
- `ConfigModelBase` — base class for config models
- `LocalizationModelBase` — base class for localization models

### Results
- `Result<T>` — discriminated union for success/failure
- `Result` — non-generic result
- `Error` — structured error with `ErrorCode`

### Health Checks
- `HealthStatus` — Healthy / Degraded / Unhealthy with message and data
- `HealthReport` — aggregated health across all components
