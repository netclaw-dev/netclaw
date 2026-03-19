namespace Netclaw.Configuration;

public enum McpCapabilityClass
{
    Unknown,
    Information,
    MemorySafe,
    SensitiveRead,
    PublishExternal,
    HighImpact
}

/// <summary>
/// Configuration for a single MCP server profile.
/// Bound from the <c>McpServers</c> section of <c>netclaw.json</c> + <c>secrets.json</c>.
/// </summary>
public sealed class McpServerEntry
{
    /// <summary>Transport type: "stdio", "sse", or "http".</summary>
    public string Transport { get; set; } = "stdio";

    /// <summary>Executable command for stdio transport.</summary>
    public string? Command { get; set; }

    /// <summary>Arguments for the stdio command.</summary>
    public string[]? Arguments { get; set; }

    /// <summary>Endpoint URL for sse/http transport.</summary>
    public string? Url { get; set; }

    /// <summary>Environment variable overlay for the MCP process.</summary>
    public Dictionary<string, string>? EnvironmentVariables { get; set; }

    /// <summary>Additional HTTP headers (for http/sse transport).</summary>
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>Whether this server is enabled. Disabled servers are skipped at startup.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>ACL grant category. Defaults to "mcp:{name}" when null.</summary>
    public string? GrantCategory { get; set; }

    /// <summary>Capability classification used by trust policy and discovery filtering.</summary>
    public McpCapabilityClass CapabilityClass { get; set; } = McpCapabilityClass.Unknown;

    /// <summary>Static OAuth client ID for servers that don't support dynamic client registration.</summary>
    public string? OAuthClientId { get; set; }

    /// <summary>Space-separated OAuth scopes to request (optional override).</summary>
    public string? OAuthScope { get; set; }
}
