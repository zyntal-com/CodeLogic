using CodeLogic;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Events;
using CodeLogic.Demo.Web.Application;
using CodeLogic.Demo.Web.Endpoints;
using CodeLogic.Demo.Web.Plugins;
using CodeLogic.Framework.Application.Plugins;

// ─────────────────────────────────────────────────────────────────────────────
// Program.cs — Entry point. Boot sequence + host configuration only.
//
// CodeLogic boots BEFORE the ASP.NET host so libraries and app context are
// ready before the first HTTP request. The ASP.NET host then gets access
// to the already-initialized framework via DI registration.
// ─────────────────────────────────────────────────────────────────────────────

// ── Step 1: Initialize CodeLogic ──────────────────────────────────────────
// Must happen before WebApplication.CreateBuilder so that all config,
// localization, and library state is ready when the host starts.

var clResult = await CodeLogic.CodeLogic.InitializeAsync(opts =>
{
    opts.FrameworkRootPath     = "data/codelogic";
    opts.ApplicationRootPath   = "data/app";
    opts.AppVersion            = "1.0.0";
    opts.HandleShutdownSignals = false; // ASP.NET Core manages CTRL+C
});

if (!clResult.Success || clResult.ShouldExit)
{
    Console.Error.WriteLine($"CodeLogic init failed: {clResult.Message}");
    return;
}

// ── Step 1b: Register libraries ───────────────────────────────────────────
// Demo library with three named endpoints in one config section (shared with
// the console demo). Edit it live via the /config endpoints.
await Libraries.LoadAsync<CodeLogic.Demo.Shared.EndpointsLibrary>();

// ── Step 2: Register the application ──────────────────────────────────────
CodeLogic.CodeLogic.RegisterApplication(new WebDemoApplication());

// ── Step 3: Configure + Start CodeLogic ───────────────────────────────────
await CodeLogic.CodeLogic.ConfigureAsync();
await CodeLogic.CodeLogic.StartAsync();

// ── Step 3b: Load plugins ─────────────────────────────────────────────────
// Create a PluginManager wired to the shared event bus, load in-process
// plugins, register with the runtime for health + graceful shutdown.
var pluginMgr = new PluginManager(
    CodeLogic.CodeLogic.GetEventBus(),
    new PluginOptions { PluginsDirectory = "data/plugins", EnableHotReload = false });

await LoadInProcessPluginAsync(pluginMgr, new RequestLoggerPlugin());
await LoadInProcessPluginAsync(pluginMgr, new NotificationPlugin());

CodeLogic.CodeLogic.SetPluginManager(pluginMgr);

// ── Step 4: Build the ASP.NET host ────────────────────────────────────────
var builder = WebApplication.CreateBuilder(args);

// ── DI registrations ──────────────────────────────────────────────────────
// Expose the shared event bus so endpoints and services can inject it
// instead of calling the static CodeLogic.GetEventBus() directly.
// This makes them testable and decoupled.
builder.Services.AddSingleton<IEventBus>(CodeLogic.CodeLogic.GetEventBus());

// Optionally expose the runtime itself for advanced scenarios
builder.Services.AddSingleton<ICodeLogicRuntime>(provider =>
    // The static CodeLogic facade wraps the singleton runtime — we
    // can't easily extract it, so create a new one for DI use.
    // For real apps, pass the runtime instance directly.
    throw new InvalidOperationException(
        "Use CodeLogic.GetApplicationContext() directly for now, " +
        "or wire ICodeLogicRuntime from your own CodeLogicRuntime instance."));

// ── Step 5: Build the app ─────────────────────────────────────────────────
var app = builder.Build();

// ── Step 6: Map endpoints (each file registers its own routes) ────────────
HomeEndpoints.Map(app);
HealthEndpoints.Map(app);
EventEndpoints.Map(app);
PluginEndpoints.Map(app);
ConfigEndpoints.Map(app);

// ── Step 7: Wire CodeLogic shutdown to ASP.NET host lifetime ─────────────
// When the host stops (CTRL+C, SIGTERM, etc.), stop CodeLogic gracefully.
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
    CodeLogic.CodeLogic.StopAsync().GetAwaiter().GetResult());

// ── Step 8: Run ───────────────────────────────────────────────────────────
app.Run();


// ── Helper: load an in-process plugin through the full 4-phase lifecycle ──
static async Task LoadInProcessPluginAsync(PluginManager manager, IPlugin plugin)
{
    var ctx = CodeLogic.CodeLogic.GetApplicationContext()
        ?? throw new InvalidOperationException("Application context not available.");

    var pluginDir = Path.Combine("data/plugins", plugin.Manifest.Id);
    Directory.CreateDirectory(Path.Combine(pluginDir, "logs"));

    var pluginCtx = new PluginContext
    {
        PluginId              = plugin.Manifest.Id,
        PluginDirectory       = pluginDir,
        ConfigDirectory       = pluginDir,
        LocalizationDirectory = Path.Combine(pluginDir, "localization"),
        LogsDirectory         = Path.Combine(pluginDir, "logs"),
        DataDirectory         = Path.Combine(pluginDir, "data"),
        Logger                = ctx.Logger,
        Configuration         = new CodeLogic.Core.Configuration.ConfigurationManager(pluginDir),
        Localization          = ctx.Localization,
        Events                = ctx.Events
    };

    await plugin.OnConfigureAsync(pluginCtx);
    await pluginCtx.Configuration.GenerateAllDefaultsAsync();
    await pluginCtx.Configuration.LoadAllAsync();
    await plugin.OnInitializeAsync(pluginCtx);
    await plugin.OnStartAsync(pluginCtx);

    // Register with the PluginManager so the plugin appears in GetLoadedPlugins(),
    // health checks, and is gracefully unloaded on shutdown via UnloadAllAsync().
    await manager.RegisterInProcessPluginAsync(plugin, pluginCtx);

    Console.WriteLine($"[Plugins] {plugin.Manifest.Name} loaded and started");
}
