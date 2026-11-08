using CL = CodeLogic.CodeLogic;                     // alias avoids ambiguity
using Env = CodeLogic.CodeLogicEnvironment;          // convenience alias
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using CodeLogic.Demo.Web.Config;
using CodeLogic.Demo.Web.Events;
using CodeLogic.Demo.Web.Localization;

namespace CodeLogic.Demo.Web.Endpoints;

// ─────────────────────────────────────────────────────────────────────────────
// Home endpoints — demonstrates config, localization, and logging per request.
// ─────────────────────────────────────────────────────────────────────────────

public static class HomeEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // GET / — returns site info + shows logging is wired
        app.MapGet("/", (IEventBus bus) =>
        {
            var ctx = CL.GetApplicationContext();
            if (ctx == null) return Results.Problem("CodeLogic not initialized.");

            // Publish request event — WebDemoApplication logs it at Trace level
            bus.Publish(new RequestReceivedEvent("GET", "/"));

            // Log the request at Debug level → writes to application.log
            ctx.Logger.Debug("GET / handled");

            var config  = ctx.Configuration.Get<WebConfig>();
            var strings = ctx.Localization.Get<WebStrings>();

            return Results.Ok(new
            {
                title         = config.SiteTitle,
                welcome       = string.Format(strings.Welcome, config.SiteTitle),
                language      = config.DefaultLanguage,
                machine       = Env.MachineName,
                appVersion    = Env.AppVersion,
                isDevelopment = Env.IsDevelopment,
                isDebugging   = Env.IsDebugging,
                logFile       = "data/app/logs/application.log"
            });
        })
        .WithName("Home");

        // GET /logs/demo — writes one entry at every log level to application.log
        // Useful for verifying which levels make it to disk based on current config.
        app.MapGet("/logs/demo", () =>
        {
            var ctx = CL.GetApplicationContext();
            if (ctx == null) return Results.Problem("CodeLogic not initialized.");

            // Write one entry at every level
            ctx.Logger.Trace(   "TRACE   — most verbose, step-by-step diagnostics");
            ctx.Logger.Debug(   "DEBUG   — useful in development, filtered in production");
            ctx.Logger.Info(    "INFO    — normal operational message");
            ctx.Logger.Warning( "WARNING — something unexpected, app still running");
            ctx.Logger.Error(   "ERROR   — something failed, needs attention");
            ctx.Logger.Critical("CRITICAL — severe failure");

            var activeConfig = Env.IsDevelopment
                ? "CodeLogic.Development.json (Debug+)"
                : "CodeLogic.json (Warning+)";

            return Results.Ok(new
            {
                message       = "Wrote 6 log entries to application.log",
                activeConfig,
                logFile       = "data/app/logs/application.log",
                tip           = "Set DOTNET_ENVIRONMENT=Development to see all levels. Otherwise only Warning+ writes to disk."
            });
        })
        .WithName("LogsDemo");
    }
}
