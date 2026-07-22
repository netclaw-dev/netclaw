// -----------------------------------------------------------------------
// <copyright file="GitHubCopilotDescriptor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Headers;
using System.Text.Json;
using Netclaw.Configuration;

namespace Netclaw.Providers.GitHubCopilot;

/// <summary>
/// Descriptor for the GitHub Copilot provider. Authentication is the GitHub
/// OAuth device flow (RFC 8628); the long-lived OAuth token is exchanged on
/// demand by <see cref="CopilotTokenExchanger"/> for a short-lived Copilot
/// API token used at <c>api.githubcopilot.com</c>.
/// </summary>
public sealed class GitHubCopilotDescriptor(
    HttpClient httpClient, CopilotTokenExchanger tokenExchanger) : IProviderDescriptor
{
    private readonly GitHubCopilotModelCatalog _modelCatalog = new();

    // The public Copilot API host. Standard/individual tokens are valid here;
    // org and GHE-data-residency tokens are valid only at the host reported in
    // the token exchange's endpoints.api (see CopilotTokenExchanger).
    internal const string PublicApiEndpoint = "https://api.githubcopilot.com";

    public string TypeKey => "github-copilot";
    public string DisplayName => "GitHub Copilot";
    public string DefaultEndpoint => PublicApiEndpoint;
    public string ModelListingPath => "/models";

    /// <summary>
    /// True when the operator deliberately redirected Copilot traffic to a custom
    /// host (e.g. a corporate proxy) rather than leaving it on the public API
    /// host. A deliberate override is always respected; otherwise the chat and
    /// probe host follows the token's <c>endpoints.api</c>, which GHE data
    /// residency requires (issue #1550).
    /// </summary>
    internal static bool HasCustomEndpointOverride(string? entryEndpoint) =>
        !string.IsNullOrWhiteSpace(entryEndpoint)
        && !string.Equals(entryEndpoint.TrimEnd('/'), PublicApiEndpoint, StringComparison.OrdinalIgnoreCase);

    public IProviderAuth Auth { get; } = CreateOAuthAuth(new GitHubCopilotAuthOptions());

    public static OAuthAuth CreateOAuthAuth(GitHubCopilotAuthOptions options)
    {
        var resolved = GitHubCopilotAuthResolver.Resolve(options);
        return new OAuthAuth
        {
            SupportedAuthMethods = [AuthMethod.OAuthDevice],
            TokenEndpoint = resolved.OAuthTokenEndpoint,
            DeviceEndpoint = resolved.DeviceEndpoint,

            // OAuth App client_id borrowed from the Neovim Copilot plugin. The
            // /copilot_internal/v2/token exchange endpoint is gated to a small
            // allowlist of editor-integration OAuth Apps (VS Code, Neovim,
            // JetBrains, gh CLI); a Netclaw-owned GitHub App was rejected with
            // HTTP 403 "Resource not accessible by integration" regardless of
            // configured permissions. Every community Copilot client (avante.nvim,
            // copilot.lua, CodeAlta) takes the same posture. Replace if/when
            // Netclaw gets its own OAuth App allowlisted by GitHub, or when we
            // migrate to the documented Copilot SDK pathway.
            ClientId = "Iv1.b507a08c87ecfe98",
            Scope = "read:user",
            UseProprietaryDeviceFlow = false,
        };
    }

    public static OAuthAuth CreateOAuthAuth(ProviderEntry entry) =>
        CreateOAuthAuth(GitHubCopilotAuthResolver.Resolve(entry).ToOptions());

    // Fallback model set used only when /models is unreachable so the
    // operator never sees an empty list on a transient failure.
    // Last refreshed: 2026-05-18 against api.githubcopilot.com/models.
    internal static readonly DiscoveredModel[] CuratedModels =
    [
        new() { ModelId = new("gpt-4o") },
        new() { ModelId = new("gpt-4o-mini") },
        new() { ModelId = new("gpt-5") },
        new() { ModelId = new("gpt-5-mini") },
        new() { ModelId = new("claude-sonnet-4") },
        new() { ModelId = new("o3-mini") },
    ];

    public async Task<ProviderProbeResult> ProbeAsync(
        ProviderEntry entry, CancellationToken ct = default)
    {
        if (entry.OAuthAccessToken.IsNullOrEmpty())
        {
            return new ProviderProbeResult(false,
                "GitHub OAuth token is required. Run "
                + "'netclaw provider add <name> github-copilot --auth oauth-device' to authenticate.",
                []);
        }

        var discovery = await DiscoverModelCatalogAsync(entry, ct).ConfigureAwait(false);
        var modelsResult = discovery.Result;

        if (!modelsResult.Success)
        {
            // A transient /models hiccup (timeout, 5xx, rate limit) shouldn't
            // leave the model picker empty, so fall back to the curated list. But
            // auth/client errors (revoked token, wrong tenant, bad request) are
            // real misconfigurations: surface them here so the operator fixes
            // setup instead of hitting the failure on the first chat (issue #1550).
            return modelsResult.Transient
                ? new ProviderProbeResult(true,
                    $"GitHub Copilot /models unreachable ({modelsResult.ErrorMessage}); "
                    + "using curated fallback list.",
                    CuratedModels)
                : modelsResult;
        }

        return modelsResult;
    }

    internal async Task<GitHubCopilotModelCapability> ResolveModelCapabilityAsync(
        ProviderEntry entry, string modelId)
    {
        var discovery = await DiscoverModelCatalogAsync(entry, CancellationToken.None).ConfigureAwait(false);
        if (!discovery.Result.Success || discovery.ApiEndpoint is null)
        {
            throw new InvalidOperationException(
                $"GitHub Copilot model capability discovery failed: {discovery.Result.ErrorMessage}");
        }

        var capability = _modelCatalog.Find(discovery.ApiEndpoint, modelId);
        if (capability is not null)
            return capability;

        var availableModels = discovery.Capabilities
            .Select(capability => capability.ModelId)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(id => $"- {id}");
        throw new InvalidOperationException(
            $"GitHub Copilot model '{modelId}' is not available to the authenticated account.\n\n"
            + "Available models:\n" + string.Join('\n', availableModels));
    }

    private async Task<ModelCatalogDiscovery> DiscoverModelCatalogAsync(
        ProviderEntry entry, CancellationToken ct)
    {
        CopilotToken copilot;
        try
        {
            copilot = await tokenExchanger.GetTokenAsync(entry, ct).ConfigureAwait(false);
        }
        catch (CopilotAuthExpiredException)
        {
            return ModelCatalogDiscovery.Fail(
                "GitHub Copilot authorization expired. Re-authenticate by running "
                + "'netclaw provider remove <name>' then "
                + "'netclaw provider add <name> github-copilot --auth oauth-device'.");
        }
        catch (HttpRequestException ex)
        {
            return ModelCatalogDiscovery.Fail($"GitHub Copilot token exchange failed: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return ModelCatalogDiscovery.Fail($"GitHub Copilot token exchange failed: {ex.Message}");
        }

        Uri? apiEndpoint = HasCustomEndpointOverride(entry.Endpoint)
            ? new Uri(entry.Endpoint!)
            : copilot.ApiBase;
        if (apiEndpoint is null)
        {
            return ModelCatalogDiscovery.Fail(
                "GitHub Copilot token exchange did not return an API host "
                + "(endpoints.api); cannot determine where to reach the Copilot API. "
                + "Re-authenticate the provider, or set an explicit endpoint to override the host.");
        }

        IReadOnlyList<GitHubCopilotModelCapability>? capabilities = null;
        var result = await ProbeHelpers.ExecuteProbeAsync(
            httpClient,
            "GitHub Copilot",
            DefaultEndpoint,
            ModelListingPath,
            apiEndpoint.ToString(),
            ApplyCopilotRequestHeaders(copilot.Token.Value),
            json =>
            {
                capabilities = ParseCopilotModelCapabilities(json);
                return ProjectProbeResult(capabilities);
            },
            ct).ConfigureAwait(false);

        if (result.Success && capabilities is not null)
            _modelCatalog.Store(apiEndpoint, capabilities);

        return new ModelCatalogDiscovery(result, apiEndpoint, capabilities ?? []);
    }

    private static Action<HttpRequestMessage> ApplyCopilotRequestHeaders(string copilotToken) =>
        request =>
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", copilotToken);
            request.Headers.TryAddWithoutValidation("copilot-integration-id", "vscode-chat");
            request.Headers.TryAddWithoutValidation("editor-version", $"Netclaw/{BuildInfo.Version}");
            request.Headers.TryAddWithoutValidation("openai-intent", "conversation-agent");
        };

    // One parser feeds both the picker projection and the provider-local runtime
    // catalog. capabilities.type filters non-chat models but is not treated as
    // proof that /chat/completions is supported.
    internal static ProviderProbeResult ParseCopilotModels(string json)
    {
        return ProjectProbeResult(ParseCopilotModelCapabilities(json));
    }

    internal static IReadOnlyList<GitHubCopilotModelCapability> ParseCopilotModelCapabilities(string json)
    {
        var models = new List<GitHubCopilotModelCapability>();
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in data.EnumerateArray())
            {
                var id = entry.TryGetProperty("id", out var idProp)
                    ? idProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                if (entry.TryGetProperty("model_picker_enabled", out var pickerProp)
                    && pickerProp.ValueKind == JsonValueKind.False)
                {
                    continue;
                }

                if (entry.TryGetProperty("capabilities", out var caps)
                    && caps.TryGetProperty("type", out var typeProp)
                    && typeProp.GetString() is { } type
                    && !string.Equals(type, "chat", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var endpoints = entry.TryGetProperty("supported_endpoints", out var endpointsProp)
                    && endpointsProp.ValueKind == JsonValueKind.Array
                    ? endpointsProp.EnumerateArray()
                        .Where(endpoint => endpoint.ValueKind == JsonValueKind.String)
                        .Select(endpoint => endpoint.GetString())
                        .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint))
                        .Select(endpoint => endpoint!)
                        .ToArray()
                    : [];

                models.Add(new GitHubCopilotModelCapability(
                    id,
                    endpoints.Any(endpoint => string.Equals(endpoint, "/responses", StringComparison.OrdinalIgnoreCase)),
                    endpoints.Any(endpoint => string.Equals(endpoint, "/chat/completions", StringComparison.OrdinalIgnoreCase)),
                    endpoints));
            }
        }

        return models;
    }

    private static ProviderProbeResult ProjectProbeResult(
        IReadOnlyList<GitHubCopilotModelCapability> models)
    {
        return models.Count == 0
            ? new ProviderProbeResult(true,
                "GitHub Copilot /models returned no chat-capable entries; "
                + "using curated fallback.",
                CuratedModels)
            : new ProviderProbeResult(true, null,
                models.Select(model => new DiscoveredModel { ModelId = new(model.ModelId) }).ToArray());
    }

    private sealed record ModelCatalogDiscovery(
        ProviderProbeResult Result,
        Uri? ApiEndpoint,
        IReadOnlyList<GitHubCopilotModelCapability> Capabilities)
    {
        public static ModelCatalogDiscovery Fail(string error) =>
            new(new ProviderProbeResult(false, error, []), null, []);
    }
}
