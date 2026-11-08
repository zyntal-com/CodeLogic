using CodeLogic.Demo.Web.Config;
using CodeLogic.Demo.Web.Events;
using CodeLogic.Demo.Web.Localization;
using CodeLogic.Framework.Application;

namespace CodeLogic.Demo.Web.Application;

// ─────────────────────────────────────────────────────────────────────────────
// The consuming application — implements IApplication for the web demo.
//
// Logging:
//   context.Logger writes to: data/app/logs/application.log
//   Log level from:           CodeLogic.Development.json (Development environment) or CodeLogic.json
// ─────────────────────────────────────────────────────────────────────────────

public class WebDemoApplication : IApplication
{
    public ApplicationManifest Manifest { get; } = new()
    {
        Id          = "demo.web",
        Name        = "CodeLogic Web Demo",
        Version     = "1.0.0",
        Description = "Reference implementation showing CodeLogic + ASP.NET Core",
        Author      = "CodeLogic"
    };

    // ── Phase 1: Configure ────────────────────────────────────────────────
    public async Task OnConfigureAsync(ApplicationContext context)
    {
        context.Configuration.Register<WebConfig>();
        context.Localization.Register<WebStrings>();

        context.Logger.Trace("OnConfigureAsync: registered WebConfig and WebStrings");
        context.Logger.Debug($"OnConfigureAsync: config dir = {context.ConfigDirectory}");
        context.Logger.Info($"{Manifest.Name} configured");

        await Task.CompletedTask;
    }

    // ── Phase 2: Initialize ───────────────────────────────────────────────
    public async Task OnInitializeAsync(ApplicationContext context)
    {
        var config = context.Configuration.Get<WebConfig>();

        context.Logger.Trace("OnInitializeAsync: reading WebConfig");
        context.Logger.Debug($"OnInitializeAsync: SiteTitle='{config.SiteTitle}', " +
                             $"DefaultLanguage='{config.DefaultLanguage}', " +
                             $"DetailedErrors={config.DetailedErrors}");
        context.Logger.Info($"{Manifest.Name} initialized");

        await Task.CompletedTask;
    }

    // ── Phase 3: Start ────────────────────────────────────────────────────
    public async Task OnStartAsync(ApplicationContext context)
    {
        context.Logger.Debug("OnStartAsync: subscribing to events");

        // Log every incoming request (Trace — very verbose)
        context.Events.Subscribe<RequestReceivedEvent>(e =>
            context.Logger.Trace($"Request: {OneLine(e.Method)} {OneLine(e.Path)}" +
                                 (e.UserId != null ? $" user={OneLine(e.UserId)}" : "")));

        // Log app notifications at appropriate level based on severity
        // Title and message can come from an HTTP caller: strip control characters so a payload
        // cannot forge extra log lines. (The /events endpoint never accepts "error" severity.)
        context.Events.Subscribe<AppNotificationEvent>(e =>
        {
            var msg = $"Notification [{OneLine(e.Severity)}] {OneLine(e.Title)}: {OneLine(e.Message)}";
            switch (e.Severity.ToLowerInvariant())
            {
                case "error":    context.Logger.Error(msg);   break;
                case "warn":
                case "warning":  context.Logger.Warning(msg); break;
                default:         context.Logger.Info(msg);    break;
            }
        });

        context.Events.Publish(new AppNotificationEvent(
            "Startup", $"{Manifest.Name} is ready to handle requests", "info"));

        context.Logger.Info($"{Manifest.Name} started");
        await Task.CompletedTask;
    }

    // ── Phase 4: Stop ─────────────────────────────────────────────────────
    public async Task OnStopAsync()
    {
        Console.WriteLine($"[{Manifest.Name}] Stopping gracefully...");
        await Task.CompletedTask;
    }

    /// <summary>Replaces control characters (CR, LF, ...) so caller-supplied text stays on one log line.</summary>
    private static string OneLine(string value) =>
        string.Create(value.Length, value, static (span, text) =>
        {
            for (var i = 0; i < text.Length; i++)
                span[i] = char.IsControl(text[i]) ? ' ' : text[i];
        });
}
