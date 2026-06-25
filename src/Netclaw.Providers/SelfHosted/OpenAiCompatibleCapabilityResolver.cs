// -----------------------------------------------------------------------
// <copyright file="OpenAiCompatibleCapabilityResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Providers.SelfHosted;

/// <summary>
/// Resolves model capabilities by probing an OpenAI-compatible
/// endpoint (<c>/v1/models</c> and the llama.cpp-only <c>/props</c>) and
/// dispatching to a backend strategy that knows how to parse the
/// concrete server's response shape. Strategies are evaluated in
/// priority order — ds4 first, vLLM second, llama.cpp third, generic last.
/// </summary>
public sealed class OpenAiCompatibleCapabilityResolver : IModelCapabilityResolver
{
    // Strategy order is meaningful: ds4 and vLLM signals are exact
    // (owned_by markers), llama.cpp's are looser (any /props response),
    // and the generic fallback matches anything.
    private static readonly IReadOnlyList<IOpenAiBackendStrategy> Strategies =
    [
        new Ds4BackendStrategy(),
        new VllmBackendStrategy(),
        new LlamaCppBackendStrategy(),
        new GenericOpenAiBackendStrategy(),
    ];

    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAiCompatibleCapabilityResolver> _logger;
    private readonly OpenAiCompatibleEndpoint _endpoint;

    public OpenAiCompatibleCapabilityResolver(
        HttpClient httpClient,
        ILogger<OpenAiCompatibleCapabilityResolver> logger,
        string endpoint,
        string? apiKey = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _endpoint = OpenAiCompatibleEndpoint.FromBaseUrl(endpoint, apiKey);
        _httpClient.BaseAddress ??= _endpoint.BaseUri;
    }

    public string? ProviderType => "openai-compatible";

    public async Task<ResolvedModelCapabilities?> ResolveAsync(string modelId, CancellationToken ct = default)
    {
        try
        {
            using var modelsRequest = new HttpRequestMessage(HttpMethod.Get, _endpoint.ModelsPath);
            ApplyAuth(modelsRequest);

            using var modelsResponse = await _httpClient.SendAsync(modelsRequest, ct);
            modelsResponse.EnsureSuccessStatusCode();
            var modelsJson = await modelsResponse.Content.ReadAsStringAsync(ct);

            // /props is llama.cpp-specific. vLLM 404s; treat any non-success as "no /props".
            string? propsJson = null;
            using (var propsRequest = new HttpRequestMessage(HttpMethod.Get, "/props"))
            {
                ApplyAuth(propsRequest);
                using var propsResponse = await _httpClient.SendAsync(propsRequest, ct);
                if (propsResponse.IsSuccessStatusCode)
                    propsJson = await propsResponse.Content.ReadAsStringAsync(ct);
            }

            using var modelsDoc = JsonDocument.Parse(modelsJson);
            using var propsDoc = propsJson is null ? null : JsonDocument.Parse(propsJson);
            var probe = new BackendProbe(modelId, modelsDoc.RootElement, propsDoc?.RootElement);
            return Dispatch(probe);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "OpenAI-compatible capability detection failed for {ModelId}", modelId);
            return null;
        }
    }

    /// <summary>
    /// Test seam: probe a single backend strategy against fixture JSON
    /// without standing up an HTTP server. Production code goes through
    /// <see cref="ResolveAsync"/>.
    /// </summary>
    internal ResolvedModelCapabilities? Dispatch(BackendProbe probe)
    {
        foreach (var strategy in Strategies)
        {
            if (!strategy.Matches(probe))
                continue;

            _logger.LogInformation(
                "OpenAI-compatible backend detected as {Backend} for model {ModelId}",
                strategy.Name, probe.ModelId);
            return strategy.Parse(probe);
        }
        return null;
    }

    /// <summary>
    /// Test helper that parses raw JSON strings into a <see cref="BackendProbe"/>
    /// and runs the strategy chain. Fixture caller owns the
    /// <see cref="JsonDocument"/> lifetime via the <c>using</c> blocks.
    /// </summary>
    internal static ResolvedModelCapabilities? ResolveFromProbe(string modelId, string modelsJson, string? propsJson)
    {
        using var modelsDoc = JsonDocument.Parse(modelsJson);
        using var propsDoc = propsJson is null ? null : JsonDocument.Parse(propsJson);
        var probe = new BackendProbe(modelId, modelsDoc.RootElement, propsDoc?.RootElement);
        foreach (var strategy in Strategies)
        {
            if (strategy.Matches(probe))
                return strategy.Parse(probe);
        }
        return null;
    }

    private void ApplyAuth(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_endpoint.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _endpoint.ApiKey);
    }
}
