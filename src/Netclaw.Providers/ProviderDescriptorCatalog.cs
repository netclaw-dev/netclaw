// -----------------------------------------------------------------------
// <copyright file="ProviderDescriptorCatalog.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Providers.Anthropic;
using Netclaw.Providers.GitHubCopilot;
using Netclaw.Providers.OpenAi;
using Netclaw.Providers.OpenRouter;
using Netclaw.Providers.SelfHosted;

namespace Netclaw.Providers;

/// <summary>
/// Canonical descriptor set used by CLI and daemon registration paths.
/// Keeps provider descriptor construction in one place.
/// </summary>
public sealed class ProviderDescriptorCatalog
{
    private ProviderDescriptorCatalog(
        OllamaDescriptor ollama,
        OpenAiCompatibleDescriptor openAiCompatible,
        OpenAiDescriptor openAi,
        AnthropicDescriptor anthropic,
        OpenRouterDescriptor openRouter,
        GitHubCopilotDescriptor gitHubCopilot)
    {
        Ollama = ollama;
        OpenAiCompatible = openAiCompatible;
        OpenAi = openAi;
        Anthropic = anthropic;
        OpenRouter = openRouter;
        GitHubCopilot = gitHubCopilot;
        All = [Ollama, OpenAiCompatible, OpenAi, Anthropic, OpenRouter, GitHubCopilot];
    }

    public OllamaDescriptor Ollama { get; }

    public OpenAiCompatibleDescriptor OpenAiCompatible { get; }

    public OpenAiDescriptor OpenAi { get; }

    public AnthropicDescriptor Anthropic { get; }

    public OpenRouterDescriptor OpenRouter { get; }

    public GitHubCopilotDescriptor GitHubCopilot { get; }

    public IReadOnlyList<IProviderDescriptor> All { get; }

    public static ProviderDescriptorCatalog Create(
        HttpClient httpClient,
        CopilotTokenExchanger copilotTokenExchanger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(copilotTokenExchanger);

        return new ProviderDescriptorCatalog(
            new OllamaDescriptor(httpClient),
            new OpenAiCompatibleDescriptor(httpClient),
            new OpenAiDescriptor(httpClient, timeProvider),
            new AnthropicDescriptor(httpClient),
            new OpenRouterDescriptor(httpClient),
            new GitHubCopilotDescriptor(httpClient, copilotTokenExchanger));
    }
}
