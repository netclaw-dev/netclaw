// -----------------------------------------------------------------------
// <copyright file="ProviderEntry.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Credential container for an LLM provider. Bound from the
/// "Providers" configuration section (netclaw.json + secrets.json overlay).
/// Each named entry represents a provider endpoint and its authentication.
/// Non-secret fields (Type, Endpoint, AuthMethod) live in netclaw.json.
/// Secret fields (ApiKey, OAuth tokens) live in secrets.json and use
/// <see cref="SensitiveString"/> to prevent accidental logging.
/// </summary>
public sealed class ProviderEntry
{
    public string Type { get; set; } = "ollama";
    public string Endpoint { get; set; } = "";
    public AuthMethod AuthMethod { get; set; } = AuthMethod.None;
    public SensitiveString? ApiKey { get; set; }
    public SensitiveString? OAuthAccessToken { get; set; }
    public SensitiveString? OAuthRefreshToken { get; set; }
    public DateTimeOffset? OAuthTokenExpiry { get; set; }
}
