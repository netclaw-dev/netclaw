namespace Netclaw.OpenAICompatible;

public sealed record OpenAiCompatibleEndpoint(
    Uri BaseUri,
    string ChatCompletionsPath,
    string ModelsPath,
    string? ApiKey = null)
{
    public static OpenAiCompatibleEndpoint FromBaseUrl(string endpoint, string? apiKey = null)
    {
        var baseUri = new Uri(endpoint.TrimEnd('/'));
        var basePath = baseUri.AbsolutePath.TrimEnd('/');

        if (basePath.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase)
            || basePath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return new OpenAiCompatibleEndpoint(
                baseUri,
                ChatCompletionsPath: Combine(basePath, "chat/completions"),
                ModelsPath: Combine(basePath, "models"),
                ApiKey: apiKey);
        }

        return new OpenAiCompatibleEndpoint(
            baseUri,
            ChatCompletionsPath: Combine(basePath, "api/v1/chat/completions"),
            ModelsPath: Combine(basePath, "api/v1/models"),
            ApiKey: apiKey);
    }

    private static string Combine(string basePath, string suffix)
    {
        if (string.IsNullOrWhiteSpace(basePath) || basePath == "/")
            return "/" + suffix.TrimStart('/');

        return basePath + "/" + suffix.TrimStart('/');
    }
}
