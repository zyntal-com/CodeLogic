using System.ComponentModel.DataAnnotations;
using CodeLogic.Core.Configuration;

namespace CodeLogic.Demo.Web.Config;

// ─────────────────────────────────────────────────────────────────────────────
// Application configuration model for the web demo.
//
// Generated to: data/app/config.json
// Edit that file, then call Configuration.ReloadAsync<WebConfig>() to pick it up.
// Reload replaces the instance — call Get<WebConfig>() again rather than caching it.
// ─────────────────────────────────────────────────────────────────────────────

public class WebConfig : ConfigModelBase
{
    /// <summary>Site display name used in API responses and logs.</summary>
    [Required]
    public string SiteTitle { get; set; } = "CodeLogic Web Demo";

    /// <summary>Default culture for localization when no Accept-Language is provided.</summary>
    public string DefaultLanguage { get; set; } = "en-US";

    /// <summary>Maximum number of recent events to keep in memory for the event log endpoint.</summary>
    [Range(10, 1000)]
    public int MaxEventLogSize { get; set; } = 100;

    /// <summary>Enable detailed error responses (disable in production).</summary>
    public bool DetailedErrors { get; set; } = false;
}
