// -----------------------------------------------------------------------
// <copyright file="McpOAuthTokenSet.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Per-MCP-server OAuth token storage. Serialized into <c>mcp-oauth-tokens.json</c>
/// as a <c>Dictionary&lt;string, McpOAuthTokenSet&gt;</c> keyed by server name.
/// </summary>
public sealed class McpOAuthTokenSet
{
    /// <summary>The current access token.</summary>
    [ConfigValue(Key = "AccessToken", PersistTo = ConfigPersistStore.McpOAuthTokens)]
    public SensitiveString AccessToken { get; set; } = null!;

    /// <summary>Refresh token for obtaining new access tokens (optional).</summary>
    [ConfigValue(Key = "RefreshToken", PersistTo = ConfigPersistStore.McpOAuthTokens)]
    public SensitiveString? RefreshToken { get; set; }

    /// <summary>When the access token expires (null = unknown/never).</summary>
    [ConfigValue(Key = "ExpiresAt", PersistTo = ConfigPersistStore.McpOAuthTokens)]
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Resolved client ID (from DCR or static config).</summary>
    public string? ClientId { get; set; }

    /// <summary>Canonical resource URI for RFC 8707 resource indicators.</summary>
    public string? McpServerUrl { get; set; }
}
