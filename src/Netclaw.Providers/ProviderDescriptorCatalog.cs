// -----------------------------------------------------------------------
// <copyright file="ProviderDescriptorCatalog.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Providers.Anthropic;
using Netclaw.Providers.DeepSeek;
using Netclaw.Providers.GitHubCopilot;
using Netclaw.Providers.OpenAi;
using Netclaw.Providers.OpenRouter;
using Netclaw.Providers.SelfHosted;
using Netclaw.Providers.VeniceAi;

namespace Netclaw.Providers;

/// <summary>
/// Canonical descriptor set used by CLI and daemon registration paths.
/// Keeps provider descriptor construction in one place.
/// </summary>
public sealed class ProviderDescriptorCatalog
{
    private ProviderDescriptorCatalog(IReadOnlyList<IProviderDescriptor> descriptors)
    {
        All = descriptors;
        Ollama = GetRequired<OllamaDescriptor>(descriptors);
        OpenAiCompatible = GetRequired<OpenAiCompatibleDescriptor>(descriptors);
        OpenAi = GetRequired<OpenAiDescriptor>(descriptors);
        Anthropic = GetRequired<AnthropicDescriptor>(descriptors);
        OpenRouter = GetRequired<OpenRouterDescriptor>(descriptors);
        GitHubCopilot = GetRequired<GitHubCopilotDescriptor>(descriptors);
        VeniceAi = GetRequired<VeniceAiDescriptor>(descriptors);
        DeepSeek = GetRequired<DeepSeekDescriptor>(descriptors);
    }

    public OllamaDescriptor Ollama { get; }

    public OpenAiCompatibleDescriptor OpenAiCompatible { get; }

    public OpenAiDescriptor OpenAi { get; }

    public AnthropicDescriptor Anthropic { get; }

    public OpenRouterDescriptor OpenRouter { get; }

    public GitHubCopilotDescriptor GitHubCopilot { get; }

    public VeniceAiDescriptor VeniceAi { get; }

    public DeepSeekDescriptor DeepSeek { get; }

    public IReadOnlyList<IProviderDescriptor> All { get; }

    public static ProviderDescriptorCatalog Create(
        HttpClient httpClient,
        CopilotTokenExchanger copilotTokenExchanger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(copilotTokenExchanger);

        return new ProviderDescriptorCatalog([
            new OllamaDescriptor(httpClient),
            new OpenAiCompatibleDescriptor(httpClient),
            new OpenAiDescriptor(httpClient, timeProvider),
            new AnthropicDescriptor(httpClient),
            new OpenRouterDescriptor(httpClient),
            new GitHubCopilotDescriptor(httpClient, copilotTokenExchanger),
            new VeniceAiDescriptor(httpClient),
            new DeepSeekDescriptor(httpClient),
        ]);
    }

    private static TDescriptor GetRequired<TDescriptor>(IReadOnlyList<IProviderDescriptor> descriptors)
        where TDescriptor : class, IProviderDescriptor => descriptors.OfType<TDescriptor>().Single();
}
