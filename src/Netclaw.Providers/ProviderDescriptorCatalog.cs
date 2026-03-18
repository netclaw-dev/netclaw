using Netclaw.Providers.Anthropic;
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
        OpenAiCodexDescriptor openAiCodex,
        AnthropicDescriptor anthropic,
        OpenRouterDescriptor openRouter)
    {
        Ollama = ollama;
        OpenAiCompatible = openAiCompatible;
        OpenAi = openAi;
        OpenAiCodex = openAiCodex;
        Anthropic = anthropic;
        OpenRouter = openRouter;
        All = [Ollama, OpenAiCompatible, OpenAi, OpenAiCodex, Anthropic, OpenRouter];
    }

    public OllamaDescriptor Ollama { get; }

    public OpenAiCompatibleDescriptor OpenAiCompatible { get; }

    public OpenAiDescriptor OpenAi { get; }

    public OpenAiCodexDescriptor OpenAiCodex { get; }

    public AnthropicDescriptor Anthropic { get; }

    public OpenRouterDescriptor OpenRouter { get; }

    public IReadOnlyList<IProviderDescriptor> All { get; }

    public static ProviderDescriptorCatalog Create(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        return new ProviderDescriptorCatalog(
            new OllamaDescriptor(httpClient),
            new OpenAiCompatibleDescriptor(httpClient),
            new OpenAiDescriptor(httpClient),
            new OpenAiCodexDescriptor(),
            new AnthropicDescriptor(httpClient),
            new OpenRouterDescriptor(httpClient));
    }
}
