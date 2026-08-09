// -----------------------------------------------------------------------
// <copyright file="OpenAiDescriptor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Netclaw.Configuration;

namespace Netclaw.Providers.OpenAi;

/// <summary>
/// Unified provider descriptor for OpenAI — supports both API key and OAuth (Codex) authentication.
/// </summary>
/// <remarks>
/// <para>
/// When authenticated via OAuth (ChatGPT subscription), requests route to the Codex backend
/// at <c>chatgpt.com/backend-api/codex</c>. API key authentication uses <c>api.openai.com</c>.
/// </para>
/// </remarks>
public sealed class OpenAiDescriptor : IProviderDescriptor
{
    internal const string CodexBackendEndpoint = "https://chatgpt.com/backend-api/codex";

    // The Codex backend gates model catalog entries by Codex CLI client version.
    // This must track the official @openai/codex release, not Netclaw's version.
    internal const string CodexModelCatalogClientVersion = "0.147.0";

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;

    public OpenAiDescriptor(HttpClient httpClient, TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string TypeKey => "openai";
    public string DisplayName => "OpenAI";
    public string DefaultEndpoint => "https://api.openai.com";
    public string ModelListingPath => "/v1/models";

    private static readonly IReadOnlyDictionary<string, string> CodexExtraAuthParams =
        new Dictionary<string, string>
        {
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["originator"] = "netclaw",
        };

    public IProviderAuth Auth { get; } = new MultiAuth
    {
        SupportedAuthMethods = [AuthMethod.OAuthDevice, AuthMethod.OAuthPkce, AuthMethod.ApiKey],
        GuidanceUrl = new Uri("https://platform.openai.com/api-keys"),
        OAuth = new OAuthAuth
        {
            SupportedAuthMethods = [AuthMethod.OAuthDevice, AuthMethod.OAuthPkce],
            TokenEndpoint = new Uri("https://auth.openai.com/oauth/token"),
            ClientId = "app_EMoamEEZ73f0CkXaXp7hrann",
            DeviceEndpoint = new Uri("https://auth.openai.com/api/accounts/deviceauth/usercode"),
            AuthorizationEndpoint = new Uri("https://auth.openai.com/oauth/authorize"),
            RedirectUri = new Uri("http://localhost:1455/auth/callback"),
            Scope = "openid profile email offline_access",
            PollingEndpoint = new Uri("https://auth.openai.com/api/accounts/deviceauth/token"),
            ExtraAuthParams = CodexExtraAuthParams,
            UseProprietaryDeviceFlow = true,
        },
        AuthMethodLabels = new Dictionary<AuthMethod, string>
        {
            [AuthMethod.OAuthDevice] = "ChatGPT Subscription (recommended)",
            [AuthMethod.OAuthPkce] = "ChatGPT Subscription (browser)",
            [AuthMethod.ApiKey] = "API Key (platform.openai.com)",
        },
    };

    public Task<ProviderProbeResult> ProbeAsync(
        ProviderEntry entry, CancellationToken ct = default)
    {
        if (entry.AuthMethod is AuthMethod.OAuthPkce or AuthMethod.OAuthDevice)
            return ProbeOAuthAsync(entry, ct);

        return ProbeApiKeyAsync(entry, ct);
    }

    private async Task<ProviderProbeResult> ProbeOAuthAsync(ProviderEntry entry, CancellationToken ct)
    {
        var token = entry.OAuthAccessToken?.Value;
        if (string.IsNullOrWhiteSpace(token))
            return new ProviderProbeResult(false,
                "OAuth token is required for OpenAI. Run 'netclaw provider add' with OAuth.", []);

        if (entry.OAuthTokenExpiry is { } expiry && expiry < _timeProvider.GetUtcNow())
            return new ProviderProbeResult(false,
                $"OAuth token expired {expiry:g}. Re-authenticate with 'netclaw provider fix <name>'.", []);

        var accountId = JwtAccountIdExtractor.ResolveAccountId(entry);
        if (accountId is null)
            return new ProviderProbeResult(false,
                "OpenAI OAuth login did not return a ChatGPT account ID. Re-authenticate with 'netclaw provider fix <name>'.", []);

        return await ProbeCodexModelsAsync(token, accountId, ct);
    }

    private async Task<ProviderProbeResult> ProbeCodexModelsAsync(
        string accessToken, string accountId, CancellationToken ct)
    {
        try
        {
            return await ProbeHelpers.ExecuteProbeAsync(
                _httpClient,
                "OpenAI Codex",
                CodexBackendEndpoint,
                $"/models?client_version={CodexModelCatalogClientVersion}",
                entryEndpoint: null,
                request =>
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                },
                ParseCodexModels,
                ct);
        }
        catch (JsonException ex)
        {
            return new ProviderProbeResult(false, $"invalid model response: {ex.Message}", []);
        }
        catch (InvalidOperationException ex)
        {
            return new ProviderProbeResult(false, $"invalid model response: {ex.Message}", []);
        }
    }

    internal static ProviderProbeResult ParseCodexModels(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("models", out var modelsArray)
            || modelsArray.ValueKind != JsonValueKind.Array)
        {
            return new ProviderProbeResult(false,
                "response did not contain a models array", []);
        }

        var models = new List<DiscoveredModel>();
        var missingContextWindow = new List<string>();
        var missingInputModalities = new List<string>();
        foreach (var model in modelsArray.EnumerateArray())
        {
            if (TryGetModelId(model) is not { } id)
                continue;

            if (IsHidden(model))
                continue;

            var contextWindow = ReadContextWindow(model);
            if (contextWindow is null)
                missingContextWindow.Add(id);

            var inputModalities = ReadModalities(model, "input_modalities");
            if (inputModalities is null)
                missingInputModalities.Add(id);

            // Pass modalities through exactly as reported. Do NOT substitute Text for an
            // absent value: an unknown that gets persisted as Text becomes a permanent
            // config override that beats real detection (#1290). Input is additionally
            // guarded below — the probe fails if any model omits it — so what we persist
            // is always provider-reported, never guessed.
            models.Add(new DiscoveredModel
            {
                ModelId = new(id),
                ContextWindowTokens = contextWindow,
                InputModalities = inputModalities,
                OutputModalities = ReadModalities(model, "output_modalities"),
            });
        }

        if (models.Count == 0)
        {
            return new ProviderProbeResult(false,
                "response contained no picker-visible models", []);
        }

        if (missingContextWindow.Count > 0)
        {
            return new ProviderProbeResult(false,
                "OpenAI Codex /models returned incomplete context-window metadata for: "
                + string.Join(", ", missingContextWindow), []);
        }

        if (missingInputModalities.Count > 0)
        {
            return new ProviderProbeResult(false,
                "OpenAI Codex /models returned incomplete input-modality metadata for: "
                + string.Join(", ", missingInputModalities), []);
        }

        return new ProviderProbeResult(true, null, models);
    }

    private static string? TryGetModelId(JsonElement model)
    {
        foreach (var propertyName in new[] { "slug", "id", "model" })
        {
            if (!model.TryGetProperty(propertyName, out var property)
                || property.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = property.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static bool IsHidden(JsonElement model)
    {
        if (!model.TryGetProperty("visibility", out var visibility)
            || visibility.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return visibility.GetString() is { } value
               && (string.Equals(value, "hide", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(value, "none", StringComparison.OrdinalIgnoreCase));
    }

    private static int? ReadContextWindow(JsonElement model)
        => TryReadPositiveInt32(model, "context_window")
           ?? TryReadPositiveInt32(model, "max_context_window");

    private static int? TryReadPositiveInt32(JsonElement model, string propertyName)
    {
        if (!model.TryGetProperty(propertyName, out var property))
            return null;

        long? value = property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var numericValue) => numericValue,
            JsonValueKind.String when long.TryParse(
                property.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var stringValue) => stringValue,
            _ => null,
        };

        if (value is null or <= 0)
            return null;

        return value > int.MaxValue ? int.MaxValue : (int)value;
    }

    private static ModelModality? ReadModalities(JsonElement model, string propertyName)
    {
        if (!model.TryGetProperty(propertyName, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = ModelModality.None;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            result |= item.GetString()?.ToLowerInvariant() switch
            {
                "text" => ModelModality.Text,
                "image" => ModelModality.Image,
                "audio" => ModelModality.Audio,
                "video" => ModelModality.Video,
                _ => ModelModality.None,
            };
        }

        return result == ModelModality.None ? null : result;
    }

    private Task<ProviderProbeResult> ProbeApiKeyAsync(ProviderEntry entry, CancellationToken ct)
    {
        var apiKey = entry.ApiKey?.Value;
        if (string.IsNullOrWhiteSpace(apiKey))
            return Task.FromResult(new ProviderProbeResult(false,
                "API key is required for OpenAI. Get one at https://platform.openai.com/api-keys", []));

        return ProbeHelpers.ExecuteProbeAsync(
            _httpClient,
            TypeKey,
            DefaultEndpoint,
            ModelListingPath,
            entry.Endpoint,
            request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey),
            ProbeHelpers.ParseOpenAiStyleModels,
            ct);
    }
}
