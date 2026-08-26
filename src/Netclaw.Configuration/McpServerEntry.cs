// -----------------------------------------------------------------------
// <copyright file="McpServerEntry.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Serialization;

namespace Netclaw.Configuration;

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

    /// <summary>
    /// Environment variable overlay for the MCP process. Values are wrapped in
    /// <see cref="SensitiveString"/> so that the <c>ENC:</c>-prefixed ciphertext
    /// stored in <c>secrets.json</c> is transparently decrypted during
    /// <see cref="Microsoft.Extensions.Configuration"/> binding and during
    /// <see cref="System.Text.Json"/> deserialization. Without the wrapper the
    /// daemon would forward the encrypted blob to the child process verbatim.
    /// </summary>
    public Dictionary<string, SensitiveString>? EnvironmentVariables { get; set; }

    /// <summary>
    /// Additional HTTP headers (for http/sse transport). Wrapped in
    /// <see cref="SensitiveString"/> for the same reason as
    /// <see cref="EnvironmentVariables"/>: <c>secrets.json</c> stores values
    /// encrypted with the <c>ENC:</c> prefix, and the wrapper drives the
    /// configuration binder / STJ converter to decrypt on read. A raw
    /// <c>Dictionary&lt;string, string&gt;</c> would skip decryption and the
    /// resulting <c>Authorization: ENC:…</c> header would be rejected by every
    /// server that requires the header to be valid on the first byte.
    /// </summary>
    public Dictionary<string, SensitiveString>? Headers { get; set; }

    /// <summary>Whether this server is enabled. Disabled servers are skipped at startup.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>ACL grant category. Defaults to "mcp:{name}" when null.</summary>
    public string? GrantCategory { get; set; }

    /// <summary>Static OAuth client ID for servers that don't support dynamic client registration.</summary>
    public string? OAuthClientId { get; set; }

    /// <summary>Space-separated OAuth scopes to request (optional override).</summary>
    public string? OAuthScope { get; set; }

    /// <summary>
    /// True when the operator configured an <c>Authorization</c> header for this server.
    /// The key comparison ignores case, because a hand-edited config can spell it any way.
    /// <c>JsonIgnore</c> keeps this computed value out of <c>netclaw.json</c>: the config
    /// schema sets <c>additionalProperties: false</c> and would reject the written file.
    /// </summary>
    [JsonIgnore]
    public bool HasConfiguredAuthorizationHeader
        => Headers?.Keys.Any(key =>
            string.Equals(key, "Authorization", StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>
    /// True for an OAuth-capable server, as the engineering glossary defines it: an HTTP
    /// or SSE server with no operator-configured <c>Authorization</c> header. Only such a
    /// server can hold OAuth tokens, and only such a server can run
    /// <c>netclaw mcp auth</c>. The daemon and the CLI read this one rule, so the remedy
    /// the daemon publishes and the remedy <c>netclaw doctor</c> prints cannot drift.
    /// <c>JsonIgnore</c> applies for the same reason as
    /// <see cref="HasConfiguredAuthorizationHeader"/>.
    /// </summary>
    [JsonIgnore]
    public bool IsOAuthCapable => Transport is not "stdio" && !HasConfiguredAuthorizationHeader;
}
