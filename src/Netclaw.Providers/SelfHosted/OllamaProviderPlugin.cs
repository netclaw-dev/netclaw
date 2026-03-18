using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Providers;
using OllamaSharp;

namespace Netclaw.Providers.SelfHosted;

/// <summary>
/// Daemon-side plugin for Ollama. Wraps <see cref="OllamaDescriptor"/>
/// and adds SDK client construction.
/// </summary>
public sealed class OllamaProviderPlugin : ProviderPluginBase<OllamaDescriptor>
{
    public OllamaProviderPlugin(OllamaDescriptor descriptor) : base(descriptor) { }

    public override IChatClient CreateChatClient(ProviderEntry entry, ModelReference model)
    {
        var endpoint = string.IsNullOrWhiteSpace(entry.Endpoint)
            ? new Uri(DefaultEndpoint)
            : new Uri(entry.Endpoint);
        return new OllamaApiClient(endpoint, model.ModelId);
    }
}
