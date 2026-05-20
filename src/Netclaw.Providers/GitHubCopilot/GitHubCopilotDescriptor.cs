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

    public string TypeKey => "github-copilot";
    public string DisplayName => "GitHub Copilot";
    public string DefaultEndpoint => "https://api.githubcopilot.com";
    public string ModelListingPath => "/models";

    public IProviderAuth Auth { get; } = new OAuthAuth
    {
        SupportedAuthMethods = [AuthMethod.OAuthDevice],
        TokenEndpoint = new Uri("https://github.com/login/oauth/access_token"),
        DeviceEndpoint = new Uri("https://github.com/login/device/code"),

        ClientId = "Iv23lipIurKdMkbqy6nH",
        Scope = "read:user",
        UseProprietaryDeviceFlow = false,
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
        if (entry.OAuthAccessToken.IsNullOrEmpty())
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

        var modelsResult = await ProbeHelpers.ExecuteProbeAsync(
            httpClient,
            "GitHub Copilot",
            DefaultEndpoint,
            ModelListingPath,
            entry.Endpoint,
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

    private static Action<HttpRequestMessage> ApplyCopilotRequestHeaders(string copilotToken) =>
        request =>
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", copilotToken);
            request.Headers.TryAddWithoutValidation("copilot-integration-id", "vscode-chat");
            request.Headers.TryAddWithoutValidation("editor-version", "Netclaw/1.0");
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
