using CodeLogic.Core.Configuration;

namespace CodeLogic.Demo.Web.Endpoints;

// ─────────────────────────────────────────────────────────────────────────────
// Config admin endpoints — the building blocks of a generic settings UI.
//
//   GET  /config                              → every library config section
//   GET  /config/{libraryId}/{section}        → schema + current values (secrets masked)
//   PUT  /config/{libraryId}/{section}        → save + apply live (body = section JSON)
//   POST /config/{libraryId}/{section}/reload → re-read the file from disk
//
// Use "_" as {section} for a library's root config.json.
//
// Try it with the demo endpoints library (CL.DemoEndpoints, section "endpoints"):
//   curl localhost:5000/config/CL.DemoEndpoints/endpoints
//   curl -X PUT localhost:5000/config/CL.DemoEndpoints/endpoints -H "Content-Type: application/json" \
//        -d '{"endpoints":{"Orders":{"url":"https://orders.local"},"Billing":{"url":"https://billing-v2.local"},"Search":{"url":"https://search.local"}}}'
// Only "Billing" reconnects (see the console output); Orders and Search stay connected.
// ─────────────────────────────────────────────────────────────────────────────

public static class ConfigEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // GET /config — list all sections
        app.MapGet("/config", () => Results.Ok(Libraries.AllConfigSections()))
            .WithName("ListConfigSections");

        // GET /config/{libraryId}/{section} — schema + current values
        app.MapGet("/config/{libraryId}/{section}", (string libraryId, string section) =>
        {
            var schema = Libraries.GetConfigSchema(libraryId, SectionName(section));
            return schema is null ? Results.NotFound() : Results.Ok(schema);
        })
        .WithName("GetConfigSection");

        // PUT /config/{libraryId}/{section} — validate, save, and apply live
        app.MapPut("/config/{libraryId}/{section}", async (string libraryId, string section, HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            var json = await reader.ReadToEndAsync();

            var result = await Libraries.UpdateConfigAsync(
                libraryId, SectionName(section), json, changedBy: "web-demo", request.HttpContext.RequestAborted);
            return ToHttpResult(result);
        })
        .WithName("UpdateConfigSection");

        // POST /config/{libraryId}/{section}/reload — pick up a hand-edited file
        app.MapPost("/config/{libraryId}/{section}/reload", async (string libraryId, string section, HttpContext http) =>
        {
            var result = await Libraries.ReloadConfigAsync(
                libraryId, SectionName(section), changedBy: "web-demo", http.RequestAborted);
            return ToHttpResult(result);
        })
        .WithName("ReloadConfigSection");
    }

    private static string SectionName(string routeValue) => routeValue == "_" ? string.Empty : routeValue;

    // 400 → invalid, nothing saved · 200 + applied=false → saved, but the library couldn't apply it
    private static IResult ToHttpResult(ConfigApplyResult result) =>
        !result.IsValid
            ? Results.BadRequest(new { applied = false, errors = result.Errors })
            : Results.Ok(new { applied = result.Applied, applyError = result.ApplyError?.Message });
}
