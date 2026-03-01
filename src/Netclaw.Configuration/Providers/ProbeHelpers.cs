using System.Text.Json;

namespace Netclaw.Configuration.Providers;

/// <summary>
/// Shared parsing helpers for provider probe responses.
/// </summary>
internal static class ProbeHelpers
{
    /// <summary>
    /// Parses the OpenAI-style model listing response (used by OpenRouter, Anthropic, OpenAI).
    /// Expects: { "data": [ { "id": "model-id" }, ... ] }
    /// </summary>
    public static ProviderProbeResult ParseOpenAiStyleModels(string json)
    {
        var doc = JsonDocument.Parse(json);
        var models = new List<DiscoveredModel>();

        if (doc.RootElement.TryGetProperty("data", out var dataArray))
        {
            foreach (var model in dataArray.EnumerateArray())
            {
                if (model.TryGetProperty("id", out var id))
                {
                    models.Add(new DiscoveredModel { ModelId = id.GetString()! });
                }
            }
        }

        return new ProviderProbeResult(true, null, models);
    }
}
