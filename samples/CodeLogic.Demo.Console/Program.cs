using CodeLogic;
using CodeLogic.Demo.Console.Application;
using CodeLogic.Demo.Console.Config;
using CodeLogic.Demo.Console.Events;
using CodeLogic.Demo.Shared;
using CodeLogic.Demo.Console.Localization;
using CodeLogic.Demo.Console.Plugins;
using CodeLogic.Framework.Application.Plugins;

// ─────────────────────────────────────────────────────────────────────────────
// Program.cs — Entry point. Boot sequence + main loop only.
// ─────────────────────────────────────────────────────────────────────────────

// ── Step 1: Initialize ────────────────────────────────────────────────────
var initResult = await CodeLogic.CodeLogic.InitializeAsync(opts =>
{
    opts.FrameworkRootPath     = "data/codelogic";
    opts.ApplicationRootPath   = "data/app";
    opts.AppVersion            = "1.0.0";
    opts.HandleShutdownSignals = true;
});

if (!initResult.Success || initResult.ShouldExit)
{
    Console.Error.WriteLine($"Startup failed: {initResult.Message}");
    return;
}

// ── Step 2: Register libraries (BEFORE ConfigureAsync) ────────────────────
// LibraryManager is ready after InitializeAsync. Register all libs here.
// Example — uncomment when you have a CL.SQLite reference:
//   await Libraries.LoadAsync<CL.SQLite.SQLiteLibrary>();
//   await Libraries.LoadAsync<CL.Mail.MailLibrary>();
//
// In-process demo library showing live, per-instance config reload ([C] below).
await Libraries.LoadAsync<EndpointsLibrary>();

// ── Step 3: Register the application ──────────────────────────────────────
CodeLogic.CodeLogic.RegisterApplication(new DemoApplication());

// ── Step 4: Configure ─────────────────────────────────────────────────────
// Discovers DLL libs, configures all registered libs + app in dependency order.
await CodeLogic.CodeLogic.ConfigureAsync();

// ── Step 5: Start ─────────────────────────────────────────────────────────
// Runs Configure on the app and libraries, then Initialize/Start on libraries before the app.
await CodeLogic.CodeLogic.StartAsync();

// ── Step 6: Create PluginManager + load in-process plugins ────────────────
// In a real app, plugins would be DLL files in a Plugins/ folder loaded
// via pluginMgr.LoadAllAsync(). In this demo we load them directly as
// in-process classes using LoadInProcessAsync() so the demo is self-contained.
//
// The PluginManager is wired to the shared event bus so plugins can pub/sub
// with the rest of the app. It is registered with the runtime for health
// checks and graceful shutdown.

var pluginMgr = new PluginManager(
    CodeLogic.CodeLogic.GetEventBus(),
    new PluginOptions { PluginsDirectory = "data/plugins", EnableHotReload = false });

// Load our demo plugins directly (no separate DLL needed for in-process plugins)
await LoadInProcessPluginAsync(pluginMgr, new GreetingPlugin());
await LoadInProcessPluginAsync(pluginMgr, new StatsPlugin());

// Register with the runtime — health checks + graceful shutdown
CodeLogic.CodeLogic.SetPluginManager(pluginMgr);

// ── Handle --health CLI flag ───────────────────────────────────────────────
// If the user ran: myapp --health
// Print the health report and exit (libraries are running so we can check them).
if (initResult.RunHealthCheck)
{
    var healthReport = await CodeLogic.CodeLogic.GetHealthAsync();
    Console.WriteLine(healthReport.ToConsoleString());
    await CodeLogic.CodeLogic.StopAsync();
    return;
}

// ── Main loop ─────────────────────────────────────────────────────────────
var ctx    = CodeLogic.CodeLogic.GetApplicationContext()!;
var config = ctx.Configuration.Get<DemoConfig>();
var bus    = CodeLogic.CodeLogic.GetEventBus();

// Show where the log file is so the user knows where to look
var logFile = Path.Combine(
    Path.GetFullPath("data/app/logs"),
    "application.log");

Console.WriteLine($"\nMachine : {CodeLogicEnvironment.MachineName}");
Console.WriteLine($"Version : {CodeLogicEnvironment.AppVersion}");
Console.WriteLine($"Debug   : {CodeLogicEnvironment.IsDebugging}");
Console.WriteLine($"Log     : {logFile}");
Console.WriteLine($"Level   : {(CodeLogicEnvironment.IsDevelopment ? "Debug (Development.json)" : "Warning (CodeLogic.json)")}");
Console.WriteLine();

// Subscribe to work completion events — displayed in console
using var workSub = bus.Subscribe<WorkCompletedEvent>(e =>
{
    var icon = e.Success ? "+" : "x";
    Console.WriteLine($"  [{icon}] {e.TaskName} — {e.Duration.TotalMilliseconds:0}ms");
});

Console.WriteLine("Commands:");
Console.WriteLine("  [W] Simulate work   (triggers GreetingPlugin + StatsPlugin)");
Console.WriteLine("  [L] Log all levels  (shows what writes to disk vs filtered)");
Console.WriteLine("  [H] Health report   (includes plugin health)");
Console.WriteLine("  [P] Plugin status   (list loaded plugins)");
Console.WriteLine("  [C] Change config   (moves the 'Billing' endpoint; others stay connected)");
Console.WriteLine("  [Q] Quit");
Console.WriteLine();

bool running = true;
while (running)
{
    if (!Console.KeyAvailable)
    {
        await Task.Delay(50);
        continue;
    }

    var key = Console.ReadKey(intercept: true).Key;
    switch (key)
    {
        case ConsoleKey.W:
            // Simulate work — publishes WorkCompletedEvent which DemoApplication
            // subscribes to and logs at Info level → writes to application.log
            bus.Publish(new UserActionEvent("do-work"));
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Randomly succeed or fail to show both Info and Warning in logs
            await Task.Delay(config.WorkIntervalMs);
            sw.Stop();
            var success = Random.Shared.NextDouble() > 0.3; // 70% success rate
            bus.Publish(new WorkCompletedEvent("SimulatedBatch", sw.Elapsed, success));
            break;

        case ConsoleKey.L:
            // Write one entry at every log level.
            // With debugger (Development.json): ALL levels write to disk
            // Without debugger (CodeLogic.json): only Warning+ writes to disk
            Console.WriteLine("\n  Writing all log levels to application.log...");
            DemoApplication.LogAllLevels(ctx, "Demo");
            Console.WriteLine($"  Done. Check: {logFile}");
            Console.WriteLine($"  (Current level filter: " +
                $"{(CodeLogicEnvironment.IsDebugging ? "Trace+" : "Warning+")})\n");
            break;

        case ConsoleKey.H:
            var health = await CodeLogic.CodeLogic.GetHealthAsync();
            Console.WriteLine(health.ToConsoleString());
            break;

        case ConsoleKey.C:
            // Change ONE endpoint through the admin API. EndpointsLibrary diffs old/new
            // and reconnects only "Billing"; Orders and Search are left untouched.
            // (Payload starts from defaults to keep the demo short; a real UI edits the current values.)
            var schema = Libraries.GetConfigSchema("CL.DemoEndpoints", "endpoints", includeSecrets: true);
            var current = schema?.Fields.FirstOrDefault(f => f.Path == "endpoints.Billing.url")?.CurrentValue as string;
            var nextUrl = current == "https://billing.local" ? "https://billing-v2.local" : "https://billing.local";
            var endpoints = new EndpointsConfig();
            endpoints.Endpoints["Billing"].Url = nextUrl;
            var json = System.Text.Json.JsonSerializer.Serialize(endpoints,
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

            Console.WriteLine($"\n  Moving Billing → {nextUrl}");
            var result = await Libraries.UpdateConfigAsync("CL.DemoEndpoints", "endpoints", json, changedBy: "demo");
            Console.WriteLine($"  Result: {result}");
            break;

        case ConsoleKey.P:
            // List all loaded plugins and their state
            var pm = CodeLogic.CodeLogic.GetPluginManager();
            if (pm == null) { Console.WriteLine("  No plugin manager registered."); break; }
            Console.WriteLine("\n  Loaded plugins:");
            foreach (var p in pm.GetLoadedPlugins())
                Console.WriteLine($"    [{p.State,-12}] {p.Manifest.Name} v{p.Manifest.Version} — {p.Manifest.Description}");
            Console.WriteLine();
            break;

        case ConsoleKey.Q:
            running = false;
            break;
    }
}

// ── Graceful shutdown ──────────────────────────────────────────────────────
// StopAsync() handles: app → plugins (via PluginManager) → libraries
await CodeLogic.CodeLogic.StopAsync();


// ── Helper: load an in-process plugin through the full 4-phase lifecycle ──
// Normally PluginManager loads plugins from separate DLL files.
// This helper lets us run in-process plugin classes through the same lifecycle
// so the demo is self-contained without needing extra projects/DLLs.
static async Task LoadInProcessPluginAsync(PluginManager manager, IPlugin plugin)
{
    var ctx = CodeLogic.CodeLogic.GetApplicationContext()
        ?? throw new InvalidOperationException("Application context not available.");

    // Build a PluginContext that reuses the app's paths/services
    var pluginDir = Path.Combine("data/plugins", plugin.Manifest.Id);
    Directory.CreateDirectory(pluginDir);

    var pluginCtx = new PluginContext
    {
        PluginId              = plugin.Manifest.Id,
        PluginDirectory       = pluginDir,
        ConfigDirectory       = pluginDir,
        LocalizationDirectory = Path.Combine(pluginDir, "localization"),
        LogsDirectory         = Path.Combine(pluginDir, "logs"),
        DataDirectory         = Path.Combine(pluginDir, "data"),
        Logger                = ctx.Logger,   // share app logger for demo simplicity
        Configuration         = new CodeLogic.Core.Configuration.ConfigurationManager(pluginDir),
        Localization          = ctx.Localization,
        Events                = ctx.Events
    };

    // Run the 4-phase lifecycle manually
    await plugin.OnConfigureAsync(pluginCtx);
    await pluginCtx.Configuration.GenerateAllDefaultsAsync();
    await pluginCtx.Configuration.LoadAllAsync();
    await plugin.OnInitializeAsync(pluginCtx);
    await plugin.OnStartAsync(pluginCtx);

    // Register with the PluginManager so the plugin appears in GetLoadedPlugins(),
    // health checks, and is gracefully unloaded on shutdown via UnloadAllAsync().
    await manager.RegisterInProcessPluginAsync(plugin, pluginCtx);

    Console.WriteLine($"  [Plugins] {plugin.Manifest.Name} loaded and started");
}
