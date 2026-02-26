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
    /// Known provider type identifiers. Used for validation and display.
    /// </summary>
    public static IReadOnlyList<string> KnownProviderTypes { get; } =
        ["anthropic", "openai", "openrouter", "ollama"];
}
