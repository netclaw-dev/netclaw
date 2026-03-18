using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Providers;

namespace Netclaw.Providers.OpenAi;

/// <summary>
/// Daemon-side plugin for OpenAI Codex (OAuth). Routes to
/// <c>chatgpt.com/backend-api/codex</c> using <see cref="OpenAiCodexChatClient"/>.
/// </summary>
public sealed class OpenAiCodexProviderPlugin : ProviderPluginBase<OpenAiCodexDescriptor>
{
    private readonly IHttpClientFactory _httpClientFactory;

    public OpenAiCodexProviderPlugin(
        OpenAiCodexDescriptor descriptor,
        IHttpClientFactory httpClientFactory)
        : base(descriptor)
    {
        _httpClientFactory = httpClientFactory;
    }

    public override IChatClient CreateChatClient(ProviderEntry entry, ModelReference model)
    {
        var token = entry.OAuthAccessToken?.Value;
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "OpenAI Codex requires an OAuth token. Run 'netclaw provider add' with OAuth.");

        var httpClient = _httpClientFactory.CreateClient("OpenAiCodex");
        return new OpenAiCodexChatClient(httpClient, model.ModelId, token);
    }
}
