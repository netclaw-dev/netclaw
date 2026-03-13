using System.Net.Http.Headers;
using System.Text.Json;

namespace Netclaw.OpenAICompatible;

public sealed class OpenAiCompatibleModelsClient
{
    private readonly HttpClient _httpClient;
    private readonly OpenAiCompatibleEndpoint _endpoint;

    public OpenAiCompatibleModelsClient(HttpClient httpClient, OpenAiCompatibleEndpoint endpoint)
    {
        _httpClient = httpClient;
        _endpoint = endpoint;
    }

    public async Task<string[]> ListModelIdsAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint.ModelsPath);
        ApplyAuth(request);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
            return [];

        return data.EnumerateArray()
            .Where(x => x.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            .Select(x => x.GetProperty("id").GetString()!)
            .ToArray();
    }

    private void ApplyAuth(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_endpoint.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _endpoint.ApiKey);
    }
}
