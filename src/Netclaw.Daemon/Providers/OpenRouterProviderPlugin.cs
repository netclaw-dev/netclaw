using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Configuration.Providers;
using Netclaw.Configuration.Providers.Descriptors;
using OpenAI;

namespace Netclaw.Daemon.Providers;

/// <summary>
/// Daemon-side plugin for OpenRouter. Wraps <see cref="OpenRouterDescriptor"/>
/// and adds SDK client construction with reasoning-exclude pipeline policy.
/// </summary>
public sealed class OpenRouterProviderPlugin : ProviderPluginBase<OpenRouterDescriptor>
{
    public OpenRouterProviderPlugin(OpenRouterDescriptor descriptor) : base(descriptor) { }

    public override IChatClient CreateChatClient(ProviderEntry entry, ModelReference model)
    {
        var apiKey = GetRequiredApiKey(entry, TypeKey);
        var endpoint = string.IsNullOrWhiteSpace(entry.Endpoint)
            ? new Uri(DefaultEndpoint)
            : new Uri(entry.Endpoint);

        var options = new OpenAIClientOptions { Endpoint = endpoint };
        options.AddPolicy(new OpenRouterReasoningExcludePolicy(), PipelinePosition.PerCall);
        var client = new OpenAIClient(new ApiKeyCredential(apiKey), options);

        return client.GetChatClient(model.ModelId).AsIChatClient();
    }

    public override IVendorOptionsSource? CreateVendorOptionsSource(ProviderEntry entry)
        => new OpenRouterVendorOptionsSource();
}
