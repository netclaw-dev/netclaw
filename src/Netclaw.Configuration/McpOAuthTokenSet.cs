namespace Netclaw.Configuration;

/// <summary>
/// Per-MCP-server OAuth token storage. Serialized into <c>mcp-oauth-tokens.json</c>
/// as a <c>Dictionary&lt;string, McpOAuthTokenSet&gt;</c> keyed by server name.
/// </summary>
public sealed class McpOAuthTokenSet
{
    /// <summary>The current access token.</summary>
    public SensitiveString AccessToken { get; set; } = null!;

    /// <summary>Refresh token for obtaining new access tokens (optional).</summary>
    public SensitiveString? RefreshToken { get; set; }

    /// <summary>When the access token expires (null = unknown/never).</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Resolved client ID (from DCR or static config).</summary>
    public string? ClientId { get; set; }

    /// <summary>Canonical resource URI for RFC 8707 resource indicators.</summary>
    public string? McpServerUrl { get; set; }
}
