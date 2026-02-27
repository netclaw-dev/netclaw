namespace Netclaw.Configuration;

/// <summary>
/// Static metadata about what each provider type supports.
/// Not persisted — compiled into the application. Used by the onboarding
/// wizard, model selector, and doctor to determine valid auth methods
/// and discovery capabilities for a given provider type.
/// </summary>
public static class ProviderCapabilities
{
    /// <summary>
    /// Returns the authentication methods supported by the given provider type.
    /// Methods are ordered by preference (first = recommended).
    /// </summary>
    public static IReadOnlyList<AuthMethod> GetSupportedAuthMethods(string providerType)
        => providerType.ToLowerInvariant() switch
        {
            "anthropic" => [AuthMethod.OAuthDevice, AuthMethod.ApiKey],
            "openai" => [AuthMethod.OAuthDevice, AuthMethod.ApiKey],
            "openrouter" => [AuthMethod.ApiKey],
            "ollama" => [AuthMethod.None],
            _ => [AuthMethod.ApiKey]
        };

    /// <summary>
    /// Returns true if the provider type supports runtime model discovery
    /// via a catalog API (e.g., Ollama /api/tags, OpenRouter /api/v1/models).
    /// </summary>
    public static bool SupportsModelDiscovery(string providerType)
        => providerType.ToLowerInvariant() is
            "ollama" or "openrouter" or "anthropic" or "openai";

    /// <summary>
    /// Returns the default base URL for a given provider type.
    /// Used as the fallback when no explicit endpoint is configured.
    /// </summary>
    public static string GetDefaultEndpoint(string providerType)
        => providerType.ToLowerInvariant() switch
        {
            "ollama" => "http://localhost:11434",
            "openrouter" => "https://openrouter.ai/api/v1",
            "anthropic" => "https://api.anthropic.com",
            "openai" => "https://api.openai.com",
            _ => throw new ArgumentException($"Unknown provider type: {providerType}", nameof(providerType))
        };

    /// <summary>
    /// Returns the relative path used to list models for a given provider type.
    /// Append to the base endpoint to build the full model listing URL.
    /// </summary>
    public static string GetModelListingPath(string providerType)
        => providerType.ToLowerInvariant() switch
        {
            "ollama" => "/api/tags",
            "openrouter" => "/models",
            "anthropic" => "/v1/models",
            "openai" => "/v1/models",
            _ => throw new ArgumentException($"Unknown provider type: {providerType}", nameof(providerType))
        };

    /// <summary>
    /// Known provider type identifiers. Used for validation and display.
    /// </summary>
    public static IReadOnlyList<string> KnownProviderTypes { get; } =
        ["anthropic", "openai", "openrouter", "ollama"];
}
