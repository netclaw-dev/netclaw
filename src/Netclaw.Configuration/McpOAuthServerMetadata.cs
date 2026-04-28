// -----------------------------------------------------------------------
// <copyright file="McpOAuthServerMetadata.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Cached OAuth authorization server discovery result for an MCP server.
/// Serialized into <c>mcp-oauth-metadata.json</c> as a
/// <c>Dictionary&lt;string, McpOAuthServerMetadata&gt;</c> keyed by server name.
/// </summary>
public sealed class McpOAuthServerMetadata
{
    /// <summary>The MCP server URL this metadata was discovered from.</summary>
    public string McpServerUrl { get; set; } = null!;

    /// <summary>OAuth authorization endpoint.</summary>
    public string AuthorizationEndpoint { get; set; } = null!;

    /// <summary>OAuth token endpoint.</summary>
    public string TokenEndpoint { get; set; } = null!;

    /// <summary>RFC 7591 dynamic client registration endpoint (optional).</summary>
    public string? RegistrationEndpoint { get; set; }

    /// <summary>RFC 8707 resource indicator for the MCP server.</summary>
    public string? ResourceIndicator { get; set; }

    /// <summary>Resolved client ID (from DCR or static config).</summary>
    public string? ClientId { get; set; }

    /// <summary>When this metadata was cached.</summary>
    public DateTimeOffset CachedAt { get; set; }
}
