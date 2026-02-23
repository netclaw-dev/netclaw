namespace Netclaw.App.Configuration;

/// <summary>
/// Credential container for an LLM provider. Bound from the
/// "Providers" configuration section. Each named entry represents
/// a provider endpoint and its authentication.
/// </summary>
public sealed class ProviderEntry
{
    public string Type { get; set; } = "ollama";
    public string Endpoint { get; set; } = "http://localhost:11434";
    public string? ApiKey { get; set; }
}
