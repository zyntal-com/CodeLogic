using CL = CodeLogic.CodeLogic; // alias avoids CodeLogic.CodeLogic ambiguity

namespace CodeLogic.Demo.Web.Endpoints;

// ─────────────────────────────────────────────────────────────────────────────
// Health endpoints — aggregated health report from all libraries and the app.
//
// Returns HTTP 200 when all components are healthy.
// Returns HTTP 503 when any component is degraded or unhealthy.
//
// The public endpoints report status levels only. Health messages can contain exception text,
// host names or connection details, so the detailed report is served to local requests only;
// in a real application put it behind authentication instead.
// ─────────────────────────────────────────────────────────────────────────────

public static class HealthEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // GET /health — JSON health report (status levels only)
        app.MapGet("/health", async () =>
        {
            var report = await CL.GetHealthAsync();
            var status = report.IsHealthy ? 200 : 503;

            return Results.Content(report.ToJson(includeDetails: false), "application/json", statusCode: status);
        })
        .WithName("Health")
        .WithDescription("Returns aggregated health status for all components.");

        // GET /health/details — full JSON report with messages (local requests only)
        app.MapGet("/health/details", async (HttpContext http) =>
        {
            if (!IsLocal(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var report = await CL.GetHealthAsync();
            return Results.Content(report.ToJson(), "application/json", statusCode: report.IsHealthy ? 200 : 503);
        })
        .WithName("HealthDetails")
        .WithDescription("Returns the detailed health report (local requests only).");

        // GET /health/text — plain text summary (local requests only, useful for monitoring tools on the host)
        app.MapGet("/health/text", async (HttpContext http) =>
        {
            if (!IsLocal(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var report = await CL.GetHealthAsync();
            return Results.Text(report.ToConsoleString());
        })
        .WithName("HealthText")
        .WithDescription("Returns a plain-text health summary (local requests only).");
    }

    private static bool IsLocal(HttpContext http) =>
        http.Connection.RemoteIpAddress is { } remote && System.Net.IPAddress.IsLoopback(remote);
}
