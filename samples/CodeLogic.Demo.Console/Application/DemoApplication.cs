using CodeLogic.Demo.Console.Config;
using CodeLogic.Demo.Console.Events;
using CodeLogic.Demo.Console.Localization;
using CodeLogic.Framework.Application;

namespace CodeLogic.Demo.Console.Application;

// ─────────────────────────────────────────────────────────────────────────────
// The consuming application — implements IApplication to participate in the
// CodeLogic lifecycle.
//
// Logging:
//   context.Logger writes to:  data/app/logs/application.log
//   Log level controlled by:   CodeLogic.json       → globalLevel (production)
//                              CodeLogic.Development.json → globalLevel (development)
//
//   DOTNET_ENVIRONMENT=Development → Development.json is used → all levels write to disk
//   Otherwise                      → CodeLogic.json is used   → only Warning+ writes to disk
// ─────────────────────────────────────────────────────────────────────────────

public class DemoApplication : IApplication
{
    public ApplicationManifest Manifest { get; } = new()
    {
        Id          = "demo.console",
        Name        = "CodeLogic Console Demo",
        Version     = "1.0.0",
        Description = "Reference implementation showing the CodeLogic boot pattern",
        Author      = "CodeLogic"
    };

    // Stored during Initialize, used throughout the app lifetime
    private DemoConfig _config = new();
    private DemoStrings _strings = new();

    // ── Phase 1: Configure ────────────────────────────────────────────────
    public async Task OnConfigureAsync(ApplicationContext context)
    {
        context.Configuration.Register<DemoConfig>();
        context.Localization.Register<DemoStrings>();

        // Trace — extremely verbose, use for step-by-step diagnostics
        context.Logger.Trace("OnConfigureAsync: registered DemoConfig and DemoStrings");

        // Debug — useful during development, too verbose for production
        context.Logger.Debug($"OnConfigureAsync: localization directory = {context.LocalizationDirectory}");

        // Info — normal operational messages
        context.Logger.Info($"{Manifest.Name} configured successfully");

        await Task.CompletedTask;
    }

    // ── Phase 2: Initialize ───────────────────────────────────────────────
    public async Task OnInitializeAsync(ApplicationContext context)
    {
        _config  = context.Configuration.Get<DemoConfig>();
        _strings = context.Localization.Get<DemoStrings>();

        context.Logger.Trace("OnInitializeAsync: config and localization loaded");
        context.Logger.Debug($"OnInitializeAsync: AppTitle='{_config.AppTitle}', " +
                             $"MaxItemsPerBatch={_config.MaxItemsPerBatch}, " +
                             $"WorkIntervalMs={_config.WorkIntervalMs}");
        context.Logger.Info($"{Manifest.Name} initialized");

        await Task.CompletedTask;
    }

    // ── Phase 3: Start ────────────────────────────────────────────────────
    public async Task OnStartAsync(ApplicationContext context)
    {
        context.Logger.Debug("OnStartAsync: subscribing to events");

        // Subscribe to WorkCompletedEvent — log the result at appropriate level
        context.Events.Subscribe<WorkCompletedEvent>(e =>
        {
            if (e.Success)
            {
                context.Logger.Info(
                    $"Work completed: task='{e.TaskName}' duration={e.Duration.TotalMilliseconds:0}ms");
            }
            else
            {
                // Warning — something went wrong but app is still running
                context.Logger.Warning(
                    $"Work failed: task='{e.TaskName}' duration={e.Duration.TotalMilliseconds:0}ms");
            }
        });

        // Subscribe to UserActionEvent — trace level (very verbose)
        context.Events.Subscribe<UserActionEvent>(e =>
            context.Logger.Trace($"UserAction received: action='{e.Action}'"));

        context.Events.Publish(new UserActionEvent("startup"));

        System.Console.WriteLine(string.Format(_strings.Welcome, _config.AppTitle));
        context.Logger.Info($"{Manifest.Name} started — listening for events");

        await Task.CompletedTask;
    }

    // ── Phase 4: Stop ─────────────────────────────────────────────────────
    public async Task OnStopAsync()
    {
        System.Console.WriteLine(_strings.Goodbye);
        await Task.CompletedTask;
    }

    // ── Public method called from Program.cs main loop ────────────────────
    // Shows all log levels so you can see what writes to disk vs. what is
    // filtered out based on globalLevel in CodeLogic.json/.Development.json.
    public static void LogAllLevels(ApplicationContext context, string source)
    {
        context.Logger.Trace(   $"[{source}] TRACE   — most verbose, step-by-step diagnostics");
        context.Logger.Debug(   $"[{source}] DEBUG   — useful in development, filtered in production");
        context.Logger.Info(    $"[{source}] INFO    — normal operational message");
        context.Logger.Warning( $"[{source}] WARNING — something unexpected, app still running");
        context.Logger.Error(   $"[{source}] ERROR   — something failed, needs attention");
        context.Logger.Critical($"[{source}] CRITICAL — severe failure, possible data loss");
    }
}
