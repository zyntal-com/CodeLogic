using CodeLogic.Core.Events;
using CodeLogic.Demo.Web.Events;

namespace CodeLogic.Demo.Web.Endpoints;

// ─────────────────────────────────────────────────────────────────────────────
// Event endpoints — demonstrates the event bus from HTTP requests.
//
// Shows how endpoints can publish events that the application layer
// reacts to without tight coupling between endpoint and handler code.
//
// SECURITY: publishing events is a privileged operation — handlers log the payload and plugins act on it.
// The demo accepts requests from the local machine only, limits field lengths and does not let a caller
// raise "error" notifications. In a real application require an authenticated caller instead
// (e.g. .RequireAuthorization(...) on the group) and keep the same input limits.
// ─────────────────────────────────────────────────────────────────────────────

public static class EventEndpoints
{
    private const int MaxTitleLength = 100;
    private const int MaxMessageLength = 500;
    private static readonly string[] AllowedSeverities = ["info", "warn"];

    public static void Map(IEndpointRouteBuilder app)
    {
        var events = app.MapGroup("/events").AddEndpointFilter(LocalRequestsOnly);

        // POST /events/notify — publish an app notification event
        // Body: { "title": "...", "message": "...", "severity": "info|warn" }
        events.MapPost("/notify", (NotifyRequest req, IEventBus bus) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Message))
                return Results.BadRequest(new { error = "Title and message are required." });
            if (req.Title.Length > MaxTitleLength || req.Message.Length > MaxMessageLength)
                return Results.BadRequest(new { error = $"Title is limited to {MaxTitleLength} and message to {MaxMessageLength} characters." });

            var severity = (req.Severity ?? "info").ToLowerInvariant();
            if (!AllowedSeverities.Contains(severity))
                return Results.BadRequest(new { error = $"Severity must be one of: {string.Join(", ", AllowedSeverities)}." });

            bus.Publish(new AppNotificationEvent(req.Title, req.Message, severity));

            return Results.Ok(new
            {
                published = true,
                eventType = nameof(AppNotificationEvent),
                title     = req.Title
            });
        })
        .WithName("PublishNotification")
        .WithDescription("Publishes an AppNotificationEvent to the shared event bus (local requests only).");

        // POST /events/request — simulate a request event (for demo/testing)
        events.MapPost("/request", (RequestSimulation req, IEventBus bus) =>
        {
            if ((req.Method?.Length ?? 0) > 16 || (req.Path?.Length ?? 0) > 200 || (req.UserId?.Length ?? 0) > 100)
                return Results.BadRequest(new { error = "Method, path or user id is too long." });

            bus.Publish(new RequestReceivedEvent(
                req.Method ?? "GET",
                req.Path   ?? "/simulated",
                req.UserId));

            return Results.Ok(new { published = true, eventType = nameof(RequestReceivedEvent) });
        })
        .WithName("SimulateRequest")
        .WithDescription("Simulates a request event via the event bus (local requests only).");
    }

    /// <summary>Rejects requests that do not come from the local machine.</summary>
    private static async ValueTask<object?> LocalRequestsOnly(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var remote = context.HttpContext.Connection.RemoteIpAddress;
        if (remote is null || !System.Net.IPAddress.IsLoopback(remote))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        return await next(context);
    }
}

// ── Request models ────────────────────────────────────────────────────────────

record NotifyRequest(string Title, string Message, string? Severity);
record RequestSimulation(string? Method, string? Path, string? UserId);
