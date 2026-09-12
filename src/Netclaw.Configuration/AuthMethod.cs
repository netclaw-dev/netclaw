// -----------------------------------------------------------------------
// <copyright file="AuthMethod.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Authentication methods supported by LLM providers.
/// </summary>
public enum AuthMethod
{
    /// <summary>No authentication required (e.g., local Ollama).</summary>
    None,

    /// <summary>Static API key stored in secrets.json.</summary>
    ApiKey,

    /// <summary>OAuth 2.0 device authorization grant (RFC 8628).</summary>
    OAuthDevice,

    /// <summary>OAuth 2.1 Authorization Code + PKCE (for MCP servers).</summary>
    OAuthPkce
}
