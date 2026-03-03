namespace Netclaw.Configuration;

/// <summary>
/// Configuration for the web search backend. Bound from the "Search" section
/// of netclaw.json (backend, endpoint) and secrets.json (API key).
/// </summary>
public sealed class SearchConfig
{
    /// <summary>
    /// Search backend identifier: "duckduckgo", "brave", or "searxng".
    /// </summary>
    public string Backend { get; set; } = "duckduckgo";

    /// <summary>
    /// Brave Search API subscription token. Required when Backend is "brave".
    /// Stored in secrets.json under Search.BraveApiKey.
    /// </summary>
    public SensitiveString? BraveApiKey { get; set; }

    /// <summary>
    /// SearXNG instance base URL (e.g., "http://searxng.local:8080").
    /// Required when Backend is "searxng".
    /// </summary>
    public string? SearXngEndpoint { get; set; }
}
