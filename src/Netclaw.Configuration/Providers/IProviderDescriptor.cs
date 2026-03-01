namespace Netclaw.Configuration.Providers;

/// <summary>
/// Metadata and capabilities for an LLM provider type.
/// Carries everything the CLI/TUI needs to render provider pickers,
/// auth flows, credential inputs, and guidance text.
/// </summary>
public interface IProviderDescriptor
{
    /// <summary>Provider type identifier (e.g. "ollama", "openai").</summary>
    string TypeKey { get; }

    /// <summary>Human-readable display name (e.g. "Ollama", "OpenAI").</summary>
    string DisplayName { get; }

    /// <summary>Supported authentication methods, ordered by preference.</summary>
    IReadOnlyList<AuthMethod> SupportedAuthMethods { get; }

    /// <summary>Default base URL when no explicit endpoint is configured.</summary>
    string DefaultEndpoint { get; }

    /// <summary>Relative path to the model listing API.</summary>
    string ModelListingPath { get; }

    /// <summary>
    /// Controls what credential input the TUI shows.
    /// Replaces hard-coded provider type checks in TUI pages.
    /// </summary>
    CredentialInputMode CredentialMode { get; }

    /// <summary>
    /// URL where users can get an API key. Shown in TUI guidance text.
    /// Null for providers that don't need API keys (e.g. Ollama).
    /// </summary>
    string? ApiKeyGuidanceUrl { get; }

    /// <summary>
    /// Validate credentials and discover available models.
    /// </summary>
    Task<ProviderProbeResult> ProbeAsync(
        ProviderEntry entry, CancellationToken ct = default);
}
