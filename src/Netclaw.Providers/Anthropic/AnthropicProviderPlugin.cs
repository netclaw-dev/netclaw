using Anthropic;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Providers;

namespace Netclaw.Providers.Anthropic;

/// <summary>
/// Daemon-side plugin for Anthropic. Wraps <see cref="AnthropicDescriptor"/>
/// and adds SDK client construction.
/// </summary>
public sealed class AnthropicProviderPlugin : ProviderPluginBase<AnthropicDescriptor>
{
    public AnthropicProviderPlugin(AnthropicDescriptor descriptor) : base(descriptor) { }

    public override IChatClient CreateChatClient(ProviderEntry entry, ModelReference model)
    {
        var apiKey = GetRequiredApiKey(entry, TypeKey);
        var client = new AnthropicClient(new()
        {
            ApiKey = apiKey
        });
        return client.AsIChatClient(model.ModelId);
    }
}
