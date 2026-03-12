using Netclaw.Configuration.Providers.Descriptors;

namespace Netclaw.Configuration.Providers;

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
        OpenRouterDescriptor openRouter)
    {
        Ollama = ollama;
        OpenAiCompatible = openAiCompatible;
        OpenAi = openAi;
        Anthropic = anthropic;
        OpenRouter = openRouter;
        All = [Ollama, OpenAiCompatible, OpenAi, Anthropic, OpenRouter];
    }

    public OllamaDescriptor Ollama { get; }

    public OpenAiCompatibleDescriptor OpenAiCompatible { get; }

    public OpenAiDescriptor OpenAi { get; }

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
            new AnthropicDescriptor(httpClient),
            new OpenRouterDescriptor(httpClient));
    }
}
