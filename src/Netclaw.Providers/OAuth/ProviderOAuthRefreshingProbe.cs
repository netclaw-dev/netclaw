// -----------------------------------------------------------------------
// <copyright file="ProviderOAuthRefreshingProbe.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Providers.OAuth;

/// <summary>
/// Adds persisted-provider OAuth refresh to provider probes without changing
/// temporary add/fix probes that must not write credentials before validation.
/// </summary>
public sealed class ProviderOAuthRefreshingProbe(
    ProviderDescriptorRegistry registry,
    ProviderOAuthTokenRefreshService tokenRefreshService)
    : IProviderProbe, IConfiguredProviderProbe
{
    public Task<ProviderProbeResult> ProbeAsync(
        string providerType,
        string? endpoint,
        string? apiKey,
        CancellationToken ct = default)
        => registry.ProbeAsync(providerType, endpoint, apiKey, ct);

    public Task<ProviderProbeResult> ProbeAsync(
        ProviderEntry entry,
        CancellationToken ct = default)
        => registry.ProbeAsync(entry, ct);

    public Task<ProviderProbeResult> ProbeAsync(
        string providerType,
        string? endpoint,
        string? credential,
        AuthMethod authMethod,
        CancellationToken ct = default)
        => registry.ProbeAsync(providerType, endpoint, credential, authMethod, ct);

    public async Task<ProviderProbeResult> ProbeConfiguredAsync(
        string providerName,
        ProviderEntry entry,
        CancellationToken ct = default)
    {
        if (!registry.TryGet(entry.Type, out var descriptor))
        {
            return new ProviderProbeResult(false,
                $"Unknown provider type: {entry.Type}", []);
        }

        if (entry.AuthMethod is AuthMethod.OAuthDevice or AuthMethod.OAuthPkce
            && entry.OAuthTokenExpiry is not null
            && descriptor.Auth.GetOAuthConfig() is { } oauth)
        {
            var refreshResult = await TryRefreshAsync(providerName, entry, oauth, ct);
            if (refreshResult is not null)
                return refreshResult;
        }

        return await descriptor.ProbeAsync(entry, ct);
    }

    private async Task<ProviderProbeResult?> TryRefreshAsync(
        string providerName,
        ProviderEntry entry,
        OAuthAuth oauth,
        CancellationToken ct)
    {
        try
        {
            await tokenRefreshService.GetValidAccessTokenAsync(providerName, entry, oauth, ct);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProviderOAuthRefreshRequiredException ex)
        {
            return new ProviderProbeResult(false, ex.Message, []);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return new ProviderProbeResult(false,
                $"OAuth refresh failed for provider '{providerName}': {ex.Message}", []);
        }
    }
}
