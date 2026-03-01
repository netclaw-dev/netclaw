using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Configuration.Providers;
using Netclaw.Configuration.Providers.Descriptors;
using OllamaSharp;

namespace Netclaw.Daemon.Providers;

/// <summary>
/// Daemon-side plugin for Ollama. Wraps <see cref="OllamaDescriptor"/>
/// and adds SDK client construction.
/// </summary>
public sealed class OllamaProviderPlugin : ILlmProviderPlugin
{
    private readonly OllamaDescriptor _descriptor;

    public OllamaProviderPlugin(OllamaDescriptor descriptor)
    {
        _descriptor = descriptor;
    }

    public string TypeKey => _descriptor.TypeKey;
    public string DisplayName => _descriptor.DisplayName;
    public IReadOnlyList<AuthMethod> SupportedAuthMethods => _descriptor.SupportedAuthMethods;
    public string DefaultEndpoint => _descriptor.DefaultEndpoint;
    public string ModelListingPath => _descriptor.ModelListingPath;
    public CredentialInputMode CredentialMode => _descriptor.CredentialMode;
    public string? ApiKeyGuidanceUrl => _descriptor.ApiKeyGuidanceUrl;

    public Task<ProviderProbeResult> ProbeAsync(ProviderEntry entry, CancellationToken ct = default)
        => _descriptor.ProbeAsync(entry, ct);

    public IChatClient CreateChatClient(ProviderEntry entry, ModelReference model)
    {
        var endpoint = string.IsNullOrWhiteSpace(entry.Endpoint)
            ? new Uri(DefaultEndpoint)
            : new Uri(entry.Endpoint);
        return new OllamaApiClient(endpoint, model.ModelId);
    }
}
