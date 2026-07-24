// -----------------------------------------------------------------------
// <copyright file="McpOAuthTokenSet.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Durable OAuth credentials for one configured MCP resource.
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

    /// <summary>Token type supplied by the authorization server.</summary>
    public string TokenType { get; set; } = "Bearer";

    /// <summary>Granted scope supplied by the authorization server.</summary>
    public string? Scope { get; set; }

    /// <summary>When the SDK obtained this token set.</summary>
    public DateTimeOffset ObtainedAt { get; set; }

    /// <summary>Resolved client ID (from DCR or static config).</summary>
    public string? ClientId { get; set; }

    /// <summary>DCR-issued client secret, when one was issued.</summary>
    public SensitiveString? ClientSecret { get; set; }

    /// <summary>Whether the stored client identity came from dynamic registration.</summary>
    public bool DynamicClientRegistration { get; set; }

    /// <summary>
    /// Legacy resource field. It is retained for deserialization only and is not
    /// accepted as the security binding for cached credentials.
    /// </summary>
    public string? McpServerUrl { get; set; }

    /// <summary>Canonical configured endpoint identity bound to these credentials.</summary>
    public string? ResourceIdentity { get; set; }

    /// <summary>
    /// Ownership epoch used to reject writes from retired connections and stale processes.
    /// </summary>
    public string? CredentialEpoch { get; set; }
}

/// <summary>Flow-scoped credentials that are not yet active.</summary>
public sealed class McpOAuthPendingCredential
{
    public string FlowId { get; set; } = null!;

    public DateTimeOffset ExpiresAt { get; set; }

    public McpOAuthTokenSet Credentials { get; set; } = null!;
}

/// <summary>Per-server durable active and pending OAuth state.</summary>
public sealed class McpOAuthCredentialEnvelope
{
    public McpOAuthTokenSet? Active { get; set; }

    public McpOAuthPendingCredential? Pending { get; set; }

    /// <summary>
    /// A dynamic identity rejected as invalid_client. Explicit flows continue
    /// withholding it until another dynamic identity is captured or a
    /// replacement credential set publishes.
    /// </summary>
    public string? RejectedDynamicClientId { get; set; }
}
