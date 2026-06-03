// -----------------------------------------------------------------------
// <copyright file="OllamaDescriptor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text.Json;
using Netclaw.Configuration;

namespace Netclaw.Providers.SelfHosted;

/// <summary>
/// Provider descriptor for Ollama (local inference server).
/// </summary>
public sealed class OllamaDescriptor : IProviderDescriptor
{
    public const string DefaultEndpointValue = "http://localhost:11434";

    private readonly HttpClient _httpClient;

    public OllamaDescriptor(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string TypeKey => "ollama";
    public string DisplayName => "Ollama";
    public string DefaultEndpoint => DefaultEndpointValue;
    public string ModelListingPath => "/api/tags";
    public IProviderAuth Auth { get; } = new EndpointOnlyAuth();

    public Task<ProviderProbeResult> ProbeAsync(
        ProviderEntry entry, CancellationToken ct = default)
    {
        return ProbeHelpers.ExecuteProbeAsync(
            _httpClient,
            TypeKey,
            DefaultEndpoint,
            ModelListingPath,
            entry.Endpoint,
            configureRequest: _ => { }, // No auth headers needed
            parseResponse: ParseOllamaModels,
            ct,
            timeout: ProbeTimeouts.SelfHosted);
    }

    private static ProviderProbeResult ParseOllamaModels(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var models = new List<DiscoveredModel>();

        if (doc.RootElement.TryGetProperty("models", out var modelsArray))
        {
            foreach (var model in modelsArray.EnumerateArray())
            {
                if (model.TryGetProperty("name", out var name))
                {
                    models.Add(new DiscoveredModel { ModelId = new(name.GetString()!) });
                }
            }
        }

        return new ProviderProbeResult(true, null, models);
    }
}
