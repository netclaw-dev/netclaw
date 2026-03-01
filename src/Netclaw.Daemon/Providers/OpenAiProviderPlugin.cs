using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Configuration.Providers.Descriptors;

namespace Netclaw.Daemon.Providers;

/// <summary>
/// Daemon-side plugin for OpenAI. Wraps <see cref="OpenAiDescriptor"/>
/// and adds SDK client construction.
/// </summary>
public sealed class OpenAiProviderPlugin : ProviderPluginBase<OpenAiDescriptor>
{
    public OpenAiProviderPlugin(OpenAiDescriptor descriptor) : base(descriptor) { }

    public override IChatClient CreateChatClient(ProviderEntry entry, ModelReference model)
    {
        var apiKey = GetRequiredApiKey(entry, TypeKey);
        return new OpenAI.Chat.ChatClient(model.ModelId, apiKey)
            .AsIChatClient();
    }
}
