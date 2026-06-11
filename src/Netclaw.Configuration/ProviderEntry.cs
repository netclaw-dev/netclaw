// -----------------------------------------------------------------------
// <copyright file="ProviderEntry.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;

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
    public SensitiveString? OAuthAccountId { get; set; }
    public DateTimeOffset? OAuthTokenExpiry { get; set; }

    /// <summary>
    /// Opaque provider-owned options bag bound from
    /// <c>Providers:&lt;name&gt;:VendorOptions</c>. Plugins deserialize their own
    /// typed view instead of adding provider-specific properties here.
    /// </summary>
    /// <remarks>
    /// Setter is <c>internal</c> so Microsoft.Extensions.Configuration's binder skips this property —
    /// <see cref="JsonObject"/> has multiple parameterized constructors the binder
    /// can't pick from, and we populate it ourselves in
    /// <see cref="ProviderConfigurationLoader"/>.
    /// </remarks>
    public JsonObject? VendorOptions { get; internal set; }
}
