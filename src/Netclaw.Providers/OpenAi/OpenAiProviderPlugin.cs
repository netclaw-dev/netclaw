using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Providers;
using OpenAI;

namespace Netclaw.Providers.OpenAi;

/// <summary>
/// Daemon-side plugin for OpenAI. Handles both API key and OAuth (Codex) authentication.
/// OAuth tokens route to the Codex backend; API keys use the standard endpoint.
/// </summary>
public sealed class OpenAiProviderPlugin : ProviderPluginBase<OpenAiDescriptor>
{
    public OpenAiProviderPlugin(OpenAiDescriptor descriptor) : base(descriptor) { }

    public override IChatClient CreateChatClient(ProviderEntry entry, ModelReference model)
    {
        if (entry.AuthMethod is AuthMethod.OAuthPkce or AuthMethod.OAuthDevice)
        {
            // OAuth path → Codex backend
            var token = entry.OAuthAccessToken;
            if (token is null || string.IsNullOrWhiteSpace(token.Value))
                throw new InvalidOperationException(
                    "OpenAI OAuth requires an access token. Run 'netclaw provider fix <name>'.");

            var accountId = JwtAccountIdExtractor.Extract(token.Value);
            var options = new OpenAIClientOptions
            {
                Endpoint = new Uri("https://chatgpt.com/backend-api/codex")
            };
            options.AddPolicy(new OpenAiCodexRequestPolicy(accountId), PipelinePosition.PerCall);

            return new OpenAI.Responses.ResponsesClient(
                    model.ModelId, new ApiKeyCredential(token.Value), options)
                .AsIChatClient();
        }

        // API key path → standard endpoint
        var apiKey = GetRequiredApiKey(entry, TypeKey);
        return new OpenAI.Responses.ResponsesClient(model.ModelId, apiKey)
            .AsIChatClient();
    }
}
