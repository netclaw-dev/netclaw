// -----------------------------------------------------------------------
// <copyright file="ProviderPluginBase.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Configuration.Providers;

namespace Netclaw.Providers;

/// <summary>
/// Base class for provider plugins that eliminates IProviderDescriptor
/// delegation boilerplate. Each plugin only needs to implement
/// <see cref="CreateChatClient"/>.
/// </summary>
public abstract class ProviderPluginBase<TDescriptor> : ILlmProviderPlugin
    where TDescriptor : IProviderDescriptor
{
    protected TDescriptor Descriptor { get; }

    protected ProviderPluginBase(TDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public string TypeKey => Descriptor.TypeKey;
    public string DisplayName => Descriptor.DisplayName;
    public string DefaultEndpoint => Descriptor.DefaultEndpoint;
    public string ModelListingPath => Descriptor.ModelListingPath;
    public IProviderAuth Auth => Descriptor.Auth;

    public Task<ProviderProbeResult> ProbeAsync(ProviderEntry entry, CancellationToken ct = default)
        => Descriptor.ProbeAsync(entry, ct);

    public abstract IChatClient CreateChatClient(ProviderEntry entry, ModelReference model);

    public virtual IVendorOptionsSource? CreateVendorOptionsSource(ProviderEntry entry) => null;

    public virtual ReasoningSuppressionDialect SuppressionDialect => ReasoningSuppressionDialect.None;

    /// <summary>
    /// Creates an <see cref="HttpClient"/> with a generous timeout suitable for LLM calls.
    /// The default <see cref="HttpClient.Timeout"/> of 100 seconds is far too short for
    /// large-context models — prefill alone can exceed 100 seconds on self-hosted hardware.
    /// Session-level timeouts (FirstTokenTimeout via ProcessingWatchdog)
    /// are the authoritative timeout layer; the HttpClient timeout is a last-resort safety
    /// net that should never fire before the watchdog.
    /// </summary>
    protected static HttpClient CreateLlmHttpClient(Uri? baseAddress = null)
    {
        return new HttpClient(new SessionAffinityHandler())
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromHours(1)
        };
    }

    /// <summary>
    /// Resolves the API key or OAuth token from a provider entry.
    /// Throws if neither is available.
    /// </summary>
    protected static string GetRequiredApiKey(ProviderEntry provider, string providerType)
    {
        if (!provider.ApiKey.IsNullOrEmpty())
            return provider.ApiKey.Value;
        if (!provider.OAuthAccessToken.IsNullOrEmpty())
            return provider.OAuthAccessToken.Value;
        throw new InvalidOperationException(
            $"Provider type '{providerType}' requires authentication. "
            + "Configure ApiKey or OAuthAccessToken in secrets.json.");
    }
}
