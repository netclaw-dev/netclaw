// -----------------------------------------------------------------------
// <copyright file="ITokenExchanger.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Providers;

/// <summary>
/// Exchanges a stored provider credential (typically a long-lived OAuth
/// access token on <see cref="ProviderEntry"/>) for a short-lived bearer
/// token suitable for the provider's request endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Implementations are expected to be process-singletons and to cache
/// fetched tokens in memory keyed by the input credential so the cache
/// survives across requests and across plugin/descriptor consumers within
/// the same process. Short-lived tokens MUST NOT be persisted to disk —
/// only the long-lived credential on <see cref="ProviderEntry"/> belongs
/// in the secrets store.
/// </para>
/// <para>
/// Today the only implementation is
/// <see cref="GitHubCopilot.CopilotTokenExchanger"/>. The interface exists
/// so that future providers needing transparent token refresh (for example
/// an OpenAI OAuth refresh-token flow on access-token expiry) can share a
/// contract with the descriptor/plugin wiring in
/// <see cref="ProviderDescriptorCatalog"/>.
/// </para>
/// <para>
/// Implementations SHOULD throw a provider-specific exception when the
/// stored credential has been revoked or has expired beyond what refresh
/// can recover, so callers can surface a "re-authenticate" prompt rather
/// than retrying. Transient transport failures SHOULD surface as
/// <see cref="HttpRequestException"/>.
/// </para>
/// </remarks>
public interface ITokenExchanger
{
    /// <summary>
    /// Returns a valid short-lived bearer token for the credential carried
    /// by <paramref name="entry"/>, fetching a fresh one when the cached
    /// entry is missing or near expiry.
    /// </summary>
    Task<string> GetTokenAsync(ProviderEntry entry, CancellationToken ct = default);
}

