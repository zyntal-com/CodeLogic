# CodeLogic 5 Documentation

CodeLogic is a modular .NET 11 framework for building structured, lifecycle-aware applications. It provides libraries, plugins, configuration, localization, logging, events, and health checks — all wired together through a strict boot sequence.

---

## Getting Started

| Document | Description |
|----------|-------------|
| [Getting Started](getting-started.md) | Installation, the 5-step boot sequence, first run behavior, your first library and application, and a minimal working example. |

---

## Core

The building blocks available to all components.

| Document | Description |
|----------|-------------|
| [Logging](Core/logging.md) | `ILogger`, `LogLevel`, `LoggingMode`, `LoggingOptions`, `NullLogger`, development vs production behavior, log file locations, and console output. |
| [Configuration](Core/configuration.md) | `ConfigModelBase`, `ConfigSectionAttribute`, `IConfigurationManager`, file naming, the Register → Generate → Load → Save → Reload lifecycle, live per-instance reload for libraries (`IConfigReloadable`, `ConfigDiff`), config file watching, and DataAnnotations validation. |
| [Localization](Core/localization.md) | `LocalizationModelBase`, `LocalizationSectionAttribute`, `LocalizedStringAttribute`, `ILocalizationManager`, culture fallback, file naming, reload support, and format strings. |
| [Events](Core/events.md) | `IEventBus`, `IEvent`, `IEventSubscription`, framework events reference, the `ComponentAlertEvent` bridge pattern, thread safety, and code examples. |
| [Results](Core/results.md) | `Result<T>`, `Result`, `Error`, `ErrorCode` — the framework's error handling pattern with factory methods, implicit conversions, and chaining. |

---

## Framework

Library, application, and plugin development.

| Document | Description |
|----------|-------------|
| [Libraries](Framework/libraries.md) | `ILibrary` 4-phase lifecycle, `LibraryContext`, `LibraryManifest`, `LibraryDependency`, `LibraryState`, dependency resolution, and `HealthStatus`. |
| [Application](Framework/application.md) | `IApplication` lifecycle, `ApplicationContext`, `ApplicationManifest`, lifecycle ordering relative to libraries, and `HealthCheckAsync`. |
| [Plugins](Framework/plugins.md) | `IPlugin` hot-reload lifecycle, `PluginContext`, `PluginManifest`, `PluginManager`, `FileSystemWatcher` hot-reload, `SetPluginManager`/`GetPluginManager`. |

---

## Official Libraries

| Document | Description |
|----------|-------------|
| [`CL.Storage`](https://zyntal-com.github.io/CodeLogic.Libs/libs/storage/index.html) | Provider-neutral mounted storage across local filesystems, S3-compatible services, FTP/FTPS, SFTP, WebDAV, Azure Blob, Google Cloud Storage, and OpenStack Swift, including multi-connection configuration and cross-provider transfers. |
| [CodeLogic Libraries](https://zyntal-com.github.io/CodeLogic.Libs/) | Documentation and API reference for all official `CodeLogic.*` integration packages. |

---

## Reference

Configuration files, CLI arguments, and runtime environment.

| Document | Description |
|----------|-------------|
| [CLI Arguments](Reference/cli-args.md) | All supported command-line flags: `--version`, `--info`, `--health`, `--generate-configs`, `--generate-configs-force`, `--dry-run`, and scoping. |
| [CodeLogic.json Reference](Reference/codelogic-json.md) | Complete reference for `CodeLogic.json` and `CodeLogic.Development.json` — every section and property with defaults and examples. |
| [Environment](Reference/environment.md) | `CodeLogicEnvironment` properties, `IsDevelopment` detection logic, `IsDebugging`, and `AppVersion` lifecycle. |
| [Health Checks](Reference/health-checks.md) | `HealthStatus`, `HealthReport`, `ToJson()`, `ToConsoleString()`, scheduled checks, the `--health` CLI flag, and implementing `HealthCheckAsync`. |
| [Versioning](Reference/versioning.md) | The shared `4.9.<patch>` version line, `version.txt`, CI-composed patch numbers, assembly binding identity, and the prerelease/release branch flow. |
