using System.ClientModel;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Configuration.Providers.Descriptors;
using OpenAI;

namespace Netclaw.Daemon.Providers;

/// <summary>
/// Daemon-side plugin for OpenAI-compatible endpoints such as Lemonade or vLLM.
/// </summary>
public sealed class OpenAiCompatibleProviderPlugin : ProviderPluginBase<OpenAiCompatibleDescriptor>
{
    public OpenAiCompatibleProviderPlugin(OpenAiCompatibleDescriptor descriptor) : base(descriptor) { }

    public override IChatClient CreateChatClient(ProviderEntry entry, ModelReference model)
    {
        var endpoint = string.IsNullOrWhiteSpace(entry.Endpoint)
            ? new Uri(DefaultEndpoint)
            : new Uri(entry.Endpoint);

        var options = new OpenAIClientOptions { Endpoint = endpoint };

        var credential = entry.ApiKey is { Value.Length: > 0 }
            ? new ApiKeyCredential(entry.ApiKey.Value)
            : new ApiKeyCredential("netclaw-local-openai-compatible");

        var client = new OpenAIClient(credential, options);
        return client.GetChatClient(model.ModelId).AsIChatClient();
    }
}
