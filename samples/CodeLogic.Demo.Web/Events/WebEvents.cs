using CodeLogic.Core.Events;

namespace CodeLogic.Demo.Web.Events;

// ─────────────────────────────────────────────────────────────────────────────
// Custom events for the web demo.
//
// These events are defined in the application layer and published/subscribed
// via the shared CodeLogic event bus. Any component that has access to
// IEventBus (injected from DI or via CodeLogic.GetEventBus()) can react.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Raised when an HTTP request is received.
/// Demonstrates event-driven request tracking.
/// </summary>
public record RequestReceivedEvent(
    string Method,
    string Path,
    string? UserId = null) : IEvent;

/// <summary>
/// Raised when something noteworthy happens in the application.
/// Can be triggered externally via POST /events/notify.
/// </summary>
public record AppNotificationEvent(
    string Title,
    string Message,
    string Severity = "info") : IEvent;
