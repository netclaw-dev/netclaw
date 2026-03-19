using Netclaw.Configuration;

namespace Netclaw.Providers;

/// <summary>
/// Authentication configuration for an LLM provider.
/// Implementations carry the specific endpoints, credentials, and parameters
/// needed by their auth method. The TUI switches on the concrete type to
/// decide what UI to show.
/// </summary>
public interface IProviderAuth
{
    /// <summary>Supported authentication methods, ordered by preference.</summary>
    IReadOnlyList<AuthMethod> SupportedAuthMethods { get; }
}

/// <summary>
/// Provider authenticates via API key (OpenAI, Anthropic, OpenRouter).
/// </summary>
public sealed class ApiKeyAuth : IProviderAuth
{
    public IReadOnlyList<AuthMethod> SupportedAuthMethods { get; } = [AuthMethod.ApiKey];

    /// <summary>
    /// URL where users can obtain an API key. Shown in TUI guidance text.
    /// </summary>
    public Uri? GuidanceUrl { get; init; }
}

/// <summary>
/// Provider requires no authentication — just an endpoint (Ollama, OpenAI-compatible).
/// </summary>
public sealed class EndpointOnlyAuth : IProviderAuth
{
    public IReadOnlyList<AuthMethod> SupportedAuthMethods { get; } = [AuthMethod.None];
}

/// <summary>
/// Provider authenticates via OAuth (device flow, browser PKCE, or both).
/// </summary>
public sealed class OAuthAuth : IProviderAuth
{
    public required IReadOnlyList<AuthMethod> SupportedAuthMethods { get; init; }

    /// <summary>OAuth token endpoint URL (for token exchange and refresh).</summary>
    public required Uri TokenEndpoint { get; init; }

    /// <summary>Default OAuth client_id for this provider.</summary>
    public required string ClientId { get; init; }

    /// <summary>
    /// Device authorization endpoint URL (user-code request).
    /// Null for providers that only support browser PKCE.
    /// </summary>
    public Uri? DeviceEndpoint { get; init; }

    /// <summary>
    /// OAuth authorization endpoint URL for browser-based Authorization Code + PKCE flow.
    /// Null for providers that only support device flow.
    /// </summary>
    public Uri? AuthorizationEndpoint { get; init; }

    /// <summary>
    /// OAuth redirect URI for browser-based flow callback.
    /// </summary>
    public Uri? RedirectUri { get; init; }

    /// <summary>
    /// Space-separated OAuth scopes to request during authorization.
    /// Null to omit the scope parameter (server decides default scopes).
    /// </summary>
    public string? Scope { get; init; }

    /// <summary>
    /// Polling endpoint for proprietary device flows (e.g. OpenAI's
    /// <c>/api/accounts/deviceauth/token</c>). Null for standard RFC 8628 flows
    /// where polling goes to <see cref="TokenEndpoint"/>.
    /// </summary>
    public Uri? PollingEndpoint { get; init; }

    /// <summary>
    /// Extra query parameters to include in the authorization URL.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ExtraAuthParams { get; init; }

    /// <summary>
    /// Whether this provider uses a proprietary device flow instead of RFC 8628.
    /// When true, the system uses <see cref="PollingEndpoint"/> for poll requests
    /// and performs a PKCE exchange at <see cref="TokenEndpoint"/> after polling succeeds.
    /// </summary>
    public bool UseProprietaryDeviceFlow { get; init; }
}

/// <summary>
/// Provider supports both API key and OAuth authentication.
/// Composes an <see cref="OAuthAuth"/> for OAuth config and carries
/// a <see cref="GuidanceUrl"/> for the API key path.
/// </summary>
public sealed class MultiAuth : IProviderAuth
{
    public required IReadOnlyList<AuthMethod> SupportedAuthMethods { get; init; }
    public Uri? GuidanceUrl { get; init; }
    public required OAuthAuth OAuth { get; init; }

    /// <summary>
    /// Optional per-method display labels for the TUI auth selection screen.
    /// Keys are <see cref="AuthMethod"/> values, values are human-readable labels.
    /// When null, falls back to generic labels in OAuthFlowViews.
    /// </summary>
    public IReadOnlyDictionary<AuthMethod, string>? AuthMethodLabels { get; init; }
}

public static class ProviderAuthExtensions
{
    public static OAuthAuth? GetOAuthConfig(this IProviderAuth auth) => auth switch
    {
        OAuthAuth o => o,
        MultiAuth m => m.OAuth,
        _ => null,
    };

    public static Uri? GetApiKeyGuidanceUrl(this IProviderAuth auth) => auth switch
    {
        ApiKeyAuth a => a.GuidanceUrl,
        MultiAuth m => m.GuidanceUrl,
        _ => null,
    };
}
