namespace Netclaw.Configuration.Providers;

/// <summary>
/// Controls what credential input the TUI shows for a provider.
/// </summary>
public enum CredentialInputMode
{
    /// <summary>Show API key field (OpenAI, Anthropic, OpenRouter).</summary>
    ApiKey,

    /// <summary>Show endpoint field, no auth (Ollama).</summary>
    EndpointOnly,
}
