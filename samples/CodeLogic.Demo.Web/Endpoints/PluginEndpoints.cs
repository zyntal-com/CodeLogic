using CL = CodeLogic.CodeLogic;
using CodeLogic.Core.Results;
using CodeLogic.Demo.Web.Plugins;
using CodeLogic.Framework.Application.Plugins;

namespace CodeLogic.Demo.Web.Endpoints;

// ─────────────────────────────────────────────────────────────────────────────
// Plugin endpoints — exposes data collected by the demo plugins.
//
//   GET /plugins               → list all loaded plugins and their state
//   GET /plugins/{id}          → one plugin (shows Result<T> → HTTP mapping)
//   GET /plugins/request-stats → stats from RequestLoggerPlugin
//   GET /plugins/notifications → recent notifications from NotificationPlugin
// ─────────────────────────────────────────────────────────────────────────────

public static class PluginEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // GET /plugins/{id} — FindPlugin returns Result<LoadedPlugin>; the switch must cover
        // the value, every Error and the empty result, or the build fails.
        app.MapGet("/plugins/{id}", (string id) => FindPlugin(id) switch
        {
            LoadedPlugin p => Results.Ok(new
            {
                id       = p.Manifest.Id,
                name     = p.Manifest.Name,
                version  = p.Manifest.Version,
                state    = p.State.ToString(),
                loadedAt = p.LoadedAt
            }),
            Error error => Results.Problem(error.Message, statusCode: StatusCodeFor(error), title: error.Code),
            null        => Results.Problem("Empty result."),
        })
        .WithName("GetPlugin");

        // GET /plugins — list all loaded plugins
        app.MapGet("/plugins", () =>
        {
            var pm = CL.GetPluginManager();
            if (pm == null) return Results.Ok(new { message = "No plugin manager registered." });

            var plugins = pm.GetLoadedPlugins().Select(p => new
            {
                id          = p.Manifest.Id,
                name        = p.Manifest.Name,
                version     = p.Manifest.Version,
                description = p.Manifest.Description,
                state       = p.State.ToString(),
                loadedAt    = p.LoadedAt
            });

            return Results.Ok(new { count = pm.GetLoadedPlugins().Count(), plugins });
        })
        .WithName("ListPlugins");

        // GET /plugins/request-stats — data from RequestLoggerPlugin
        app.MapGet("/plugins/request-stats", () =>
        {
            var pm = CL.GetPluginManager();
            if (pm == null) return Results.Problem("No plugin manager registered.");

            var plugin = pm.GetPlugin<RequestLoggerPlugin>("demo.request-logger");
            if (plugin == null) return Results.NotFound(new { error = "RequestLoggerPlugin not loaded." });

            var (total, byMethod, byPath) = plugin.GetStats();
            var topPaths = byPath
                .OrderByDescending(kv => kv.Value)
                .Take(5)
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            return Results.Ok(new
            {
                total,
                byMethod,
                topPaths
            });
        })
        .WithName("RequestStats");

        // GET /plugins/notifications — rolling log from NotificationPlugin
        app.MapGet("/plugins/notifications", () =>
        {
            var pm = CL.GetPluginManager();
            if (pm == null) return Results.Problem("No plugin manager registered.");

            var plugin = pm.GetPlugin<NotificationPlugin>("demo.notifications");
            if (plugin == null) return Results.NotFound(new { error = "NotificationPlugin not loaded." });

            var log = plugin.GetLog();
            return Results.Ok(new
            {
                count = log.Count,
                notifications = log.Select(n => new
                {
                    timestamp = n.Timestamp,
                    severity  = n.Severity,
                    title     = n.Title,
                    message   = n.Message
                })
            });
        })
        .WithName("Notifications");
    }

    // Expected failures are Error values, not exceptions.
    private static Result<LoadedPlugin> FindPlugin(string id)
    {
        var pm = CL.GetPluginManager();
        if (pm is null)
            return Error.Unavailable("plugins.no_manager", "No plugin manager registered.");

        var plugin = pm.GetLoadedPlugins().FirstOrDefault(p => p.Manifest.Id == id);
        return plugin is null
            ? Error.NotFound("plugins.not_found", $"Plugin '{id}' is not loaded.")
            : plugin;
    }

    // Error is a closed hierarchy, so this switch needs no default arm.
    private static int StatusCodeFor(Error error) => error switch
    {
        NotFoundError     => StatusCodes.Status404NotFound,
        ValidationError   => StatusCodes.Status422UnprocessableEntity,
        UnauthorizedError => StatusCodes.Status401Unauthorized,
        ConflictError     => StatusCodes.Status409Conflict,
        TimeoutError      => StatusCodes.Status504GatewayTimeout,
        UnavailableError  => StatusCodes.Status503ServiceUnavailable,
        InternalError     => StatusCodes.Status500InternalServerError,
    };
}
