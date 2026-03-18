using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Providers;

namespace Netclaw.Providers.SelfHosted;

/// <summary>
/// Daemon-side plugin for OpenAI-compatible endpoints such as Lemonade or vLLM.
/// </summary>
public sealed class OpenAiCompatibleProviderPlugin : ProviderPluginBase<OpenAiCompatibleDescriptor>
{
    public OpenAiCompatibleProviderPlugin(OpenAiCompatibleDescriptor descriptor) : base(descriptor) { }

    public override IChatClient CreateChatClient(ProviderEntry entry, ModelReference model)
    {
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl(
            entry.Endpoint ?? DefaultEndpoint,
            entry.ApiKey?.Value);

        return new OpenAiCompatibleChatClient(new HttpClient { BaseAddress = endpoint.BaseUri }, endpoint, model.ModelId);
    }
}
