// -----------------------------------------------------------------------
// <copyright file="GitHubCopilotDescriptor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Headers;
using System.Text.Json;
using Netclaw.Configuration;
using Netclaw.Configuration.Providers;

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

    public string TypeKey => "github-copilot";
    public string DisplayName => "GitHub Copilot";
    public string DefaultEndpoint => "https://api.githubcopilot.com";
    public string ModelListingPath => "/models";

    public IProviderAuth Auth { get; } = new MultiAuth
    {
        SupportedAuthMethods = [AuthMethod.OAuthDevice, AuthMethod.ApiKey],
        OAuth = CreateOAuthAuth(new GitHubCopilotAuthOptions()),
        AuthMethodLabels = new Dictionary<AuthMethod, string>
        {
            [AuthMethod.OAuthDevice] = "GitHub OAuth device flow",
            [AuthMethod.ApiKey] = "GitHub token or environment token",
        },
    };

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
        var options = ResolveOptions(entry);
        if (options.AuthMode == GitHubCopilotAuthMode.OAuthDevice
            && entry.OAuthAccessToken.IsNullOrEmpty())
        {
            return new ProviderProbeResult(false,
                "GitHub OAuth token is required. Run "
                + "'netclaw provider add <name> github-copilot --auth oauth-device' to authenticate.",
                []);
        }

        string copilotToken;
        try
        {
            copilotToken = await tokenExchanger.GetTokenAsync(entry, ct);
        }
        catch (CopilotAuthExpiredException)
        {
            return new ProviderProbeResult(false,
                "GitHub Copilot authorization expired. Re-authenticate by running "
                + "'netclaw provider remove <name>' then "
                + "'netclaw provider add <name> github-copilot --auth oauth-device'.",
                []);
        }
        catch (HttpRequestException ex)
        {
            return new ProviderProbeResult(false,
                $"GitHub Copilot token exchange failed: {ex.Message}",
                []);
        }
        catch (InvalidOperationException ex)
        {
            return new ProviderProbeResult(false,
                $"GitHub Copilot token exchange failed: {ex.Message}",
                []);
        }
        catch (NotSupportedException ex)
        {
            return new ProviderProbeResult(false,
                $"GitHub Copilot token exchange failed: {ex.Message}",
                []);
        }

        var modelsResult = await ProbeHelpers.ExecuteProbeAsync(
            httpClient,
            "GitHub Copilot",
            options.CopilotApiBase.ToString().TrimEnd('/'),
            ModelListingPath,
            NormalizeCopilotEndpointOverride(entry.Endpoint),
            ApplyCopilotRequestHeaders(copilotToken),
            ParseCopilotModels,
            ct);

        if (!modelsResult.Success)
        {
            return new ProviderProbeResult(true,
                $"GitHub Copilot /models unreachable ({modelsResult.ErrorMessage}); "
                + "using curated fallback list.",
                CuratedModels);
        }

        return modelsResult;
    }

    public static OAuthAuth CreateOAuthAuth(GitHubCopilotAuthOptions options) => new()
    {
        SupportedAuthMethods = [AuthMethod.OAuthDevice],
        TokenEndpoint = options.OAuthTokenEndpoint,
        DeviceEndpoint = options.OAuthDeviceEndpoint,

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

    internal static string? NormalizeCopilotEndpointOverride(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return null;

        var normalized = endpoint.TrimEnd('/');
        return string.Equals(normalized, new GitHubCopilotAuthOptions().CopilotApiBase.ToString().TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase)
            ? null
            : normalized;
    }

    public static GitHubCopilotAuthOptions ResolveOptions(ProviderEntry entry)
    {
        var options = entry.GetVendorOptions<GitHubCopilotAuthOptions>() ?? new GitHubCopilotAuthOptions();
        options = ApplyEnvironmentHostDefaults(options);
        if (entry.AuthMethod != AuthMethod.ApiKey
            || options.AuthMode != GitHubCopilotAuthMode.OAuthDevice)
        {
            return options;
        }

        return new GitHubCopilotAuthOptions
        {
            CopilotApiBase = options.CopilotApiBase,
            GitHubHost = options.GitHubHost,
            GitHubApiBase = options.GitHubApiBase,
            CopilotTokenExchangePath = options.CopilotTokenExchangePath,
            AuthMode = GitHubCopilotAuthMode.ApiKey,
            GitHubToken = options.GitHubToken,
            TokenEnvVars = options.TokenEnvVars,
        };
    }

    private static GitHubCopilotAuthOptions ApplyEnvironmentHostDefaults(GitHubCopilotAuthOptions options)
    {
        var defaultOptions = new GitHubCopilotAuthOptions();
        var gitHubHost = options.GitHubHost == defaultOptions.GitHubHost
            ? ReadUriEnvironment("COPILOT_GH_HOST", "GH_HOST", "GHE_HOST", "GITHUB_SERVER_URL") ?? options.GitHubHost
            : options.GitHubHost;
        var gitHubApiBase = options.GitHubApiBase == defaultOptions.GitHubApiBase
            ? ReadUriEnvironment("GITHUB_API_URL") ?? BuildApiBaseFromHost(gitHubHost) ?? options.GitHubApiBase
            : options.GitHubApiBase;

        if (gitHubHost == options.GitHubHost && gitHubApiBase == options.GitHubApiBase)
            return options;

        return new GitHubCopilotAuthOptions
        {
            CopilotApiBase = options.CopilotApiBase,
            GitHubHost = gitHubHost,
            GitHubApiBase = gitHubApiBase,
            CopilotTokenExchangePath = options.CopilotTokenExchangePath,
            AuthMode = options.AuthMode,
            GitHubToken = options.GitHubToken,
            TokenEnvVars = options.TokenEnvVars,
        };
    }

    private static Uri? BuildApiBaseFromHost(Uri gitHubHost)
    {
        if (string.Equals(gitHubHost.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            return null;

        return new Uri($"https://api.{gitHubHost.Host}");
    }

    private static Uri? ReadUriEnvironment(params string[] names)
    {
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var normalized = value.Contains("://", StringComparison.Ordinal)
                ? value.Trim()
                : $"https://{value.Trim()}";
            if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
                return uri;

            throw new InvalidOperationException(
                $"Environment variable {name} must be an absolute GitHub host URI or hostname.");
        }

        return null;
    }

    private static Action<HttpRequestMessage> ApplyCopilotRequestHeaders(string copilotToken) =>
        request =>
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", copilotToken);
            request.Headers.TryAddWithoutValidation("copilot-integration-id", "vscode-chat");
            request.Headers.TryAddWithoutValidation("editor-version", $"Netclaw/{BuildInfo.Version}");
            request.Headers.TryAddWithoutValidation("openai-intent", "conversation-agent");
        };

    // Filters the Copilot /models payload to chat-capable entries the
    // server marks as picker-eligible. Falls back to the curated list when
    // every entry is filtered out so callers never see a zero-model success.
    internal static ProviderProbeResult ParseCopilotModels(string json)
    {
        var models = new List<DiscoveredModel>();
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

                models.Add(new DiscoveredModel { ModelId = new(id) });
            }
        }

        return models.Count == 0
            ? new ProviderProbeResult(true,
                "GitHub Copilot /models returned no chat-capable entries; "
                + "using curated fallback.",
                CuratedModels)
            : new ProviderProbeResult(true, null, models);
    }
}
