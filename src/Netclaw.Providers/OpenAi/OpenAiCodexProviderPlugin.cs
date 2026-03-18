using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Providers;
using OpenAI;

namespace Netclaw.Providers.OpenAi;

/// <summary>
/// Daemon-side plugin for OpenAI Codex (OAuth). Routes to
/// <c>chatgpt.com/backend-api/codex</c> using the SDK's <see cref="OpenAI.Responses.ResponsesClient"/>
/// with a <see cref="OpenAiCodexRequestPolicy"/> for header injection and store suppression.
/// </summary>
public sealed class OpenAiCodexProviderPlugin : ProviderPluginBase<OpenAiCodexDescriptor>
{
    public OpenAiCodexProviderPlugin(OpenAiCodexDescriptor descriptor) : base(descriptor) { }

    public override IChatClient CreateChatClient(ProviderEntry entry, ModelReference model)
    {
        var token = entry.OAuthAccessToken;
        if (token is null || string.IsNullOrWhiteSpace(token.Value))
            throw new InvalidOperationException(
                "OpenAI Codex requires an OAuth token. Run 'netclaw provider add' with OAuth.");

        var accountId = JwtAccountIdExtractor.Extract(token.Value);
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri("https://chatgpt.com/backend-api/codex")
        };
        options.AddPolicy(new OpenAiCodexRequestPolicy(accountId), PipelinePosition.PerCall);

        return new OpenAI.Responses.ResponsesClient(
                model.ModelId,
                new ApiKeyCredential(token.Value),
                options)
            .AsIChatClient();
    }
}
