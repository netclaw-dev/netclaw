using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Configuration.Providers;
using Netclaw.Configuration.Providers.Descriptors;

namespace Netclaw.Daemon.Providers;

/// <summary>
/// Daemon-side plugin for OpenAI. Wraps <see cref="OpenAiDescriptor"/>
/// and adds SDK client construction.
/// </summary>
public sealed class OpenAiProviderPlugin : ILlmProviderPlugin
{
    private readonly OpenAiDescriptor _descriptor;

    public OpenAiProviderPlugin(OpenAiDescriptor descriptor)
    {
        _descriptor = descriptor;
    }

    public string TypeKey => _descriptor.TypeKey;
    public string DisplayName => _descriptor.DisplayName;
    public IReadOnlyList<AuthMethod> SupportedAuthMethods => _descriptor.SupportedAuthMethods;
    public string DefaultEndpoint => _descriptor.DefaultEndpoint;
    public string ModelListingPath => _descriptor.ModelListingPath;
    public CredentialInputMode CredentialMode => _descriptor.CredentialMode;
    public string? ApiKeyGuidanceUrl => _descriptor.ApiKeyGuidanceUrl;

    public Task<ProviderProbeResult> ProbeAsync(ProviderEntry entry, CancellationToken ct = default)
        => _descriptor.ProbeAsync(entry, ct);

    public IChatClient CreateChatClient(ProviderEntry entry, ModelReference model)
    {
        var apiKey = GetRequiredApiKey(entry);
        return new OpenAI.Chat.ChatClient(model.ModelId, apiKey)
            .AsIChatClient();
    }

    private static string GetRequiredApiKey(ProviderEntry provider)
    {
        if (provider.ApiKey is { } apiKey && !string.IsNullOrWhiteSpace(apiKey.Value))
            return apiKey.Value;
        if (provider.OAuthAccessToken is { } oauthToken && !string.IsNullOrWhiteSpace(oauthToken.Value))
            return oauthToken.Value;
        throw new InvalidOperationException(
            "Provider type 'openai' requires authentication. "
            + "Configure ApiKey or OAuthAccessToken in secrets.json.");
    }
}
