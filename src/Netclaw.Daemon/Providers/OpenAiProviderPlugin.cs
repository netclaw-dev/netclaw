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
        // Use the Responses API for all OpenAI requests — works with both OAuth tokens
        // and API keys. Codex OAuth tokens were never granted Chat Completions access;
        // calling /v1/chat/completions with them returns 429 "insufficient_quota".
        // The Responses API (/v1/responses) is the only completions endpoint authorized
        // for these tokens. API keys work with both endpoints.
        // See: https://developers.openai.com/docs/guides/migrate-to-responses
        return new OpenAI.Responses.ResponsesClient(model.ModelId, apiKey)
            .AsIChatClient();
    }
}
