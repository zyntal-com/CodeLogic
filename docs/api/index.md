# API Reference

This section contains the complete API reference for CodeLogic 5, generated from XML documentation comments in the source code.

## Namespaces

| Namespace | Description |
|-----------|-------------|
| `CodeLogic` | Entry point (`CodeLogic` static class), `Libraries`, `CodeLogicEnvironment`, `ICodeLogicRuntime`, `CodeLogicOptions`, `CodeLogicConfiguration`, `HealthReport` |
| `CodeLogic.Core.Configuration` | `ConfigModelBase`, `IConfigurationManager`, schemas, `ConfigDiff` |
| `CodeLogic.Core.Events` | `IEventBus`, `IEvent`, `FrameworkEvent` and the framework events |
| `CodeLogic.Core.Localization` | `LocalizationModelBase`, `ILocalizationManager` |
| `CodeLogic.Core.Logging` | `ILogger`, `Logger`, `LoggingOptions` |
| `CodeLogic.Core.Results` | `Result<T>`, `Result`, `Ok`, `Error` and its subtypes, `ErrorCode` |
| `CodeLogic.Core.Utilities` | `SemanticVersion`, `CliArgParser`, `StartupValidator`, `FirstRunManager` |
| `CodeLogic.Framework.Libraries` | `ILibrary`, `LibraryContext`, `LibraryManifest`, `LibraryManager`, `HealthStatus` |
| `CodeLogic.Framework.Application` | `IApplication`, `ApplicationContext`, `ApplicationManifest` |
| `CodeLogic.Framework.Application.Plugins` | `IPlugin`, `PluginContext`, `PluginManifest`, `PluginManager` |

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
- `FrameworkEvent` — closed base of every framework event
- `ILogger` — structured logger
- `ConfigModelBase` — base class for config models
- `LocalizationModelBase` — base class for localization models

### Results
- `Result<T>` — union of `T | Error`
- `Result` — union of `Ok | Error`
- `Error` — closed record with one subtype per kind (`NotFoundError`, `ValidationError`, ...)

### Health Checks
- `HealthStatus` — Healthy / Degraded / Unhealthy with message and data
- `HealthReport` — aggregated health across all components
