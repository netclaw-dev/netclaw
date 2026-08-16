// -----------------------------------------------------------------------
// <copyright file="OpenAiCompatibleEndpoint.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Providers.SelfHosted;

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

        // A trailing version segment (v1, v4, ...) means the operator already
        // pinned an API version — appending another "v1/..." would produce a
        // /v4/v1/chat/completions 404 on hosts like api.z.ai. Bare hosts and
        // unversioned paths keep the /v1 default below.
        if (HasVersionedSuffix(basePath))
        {
            return new OpenAiCompatibleEndpoint(
                baseUri,
                ChatCompletionsPath: Combine(basePath, "chat/completions"),
                ModelsPath: Combine(basePath, "models"),
                ApiKey: apiKey);
        }

        return new OpenAiCompatibleEndpoint(
            baseUri,
            ChatCompletionsPath: Combine(basePath, "v1/chat/completions"),
            ModelsPath: Combine(basePath, "v1/models"),
            ApiKey: apiKey);
    }

    private static bool HasVersionedSuffix(string basePath)
    {
        var lastSlash = basePath.LastIndexOf('/');
        if (lastSlash < 0)
            return false;

        var segment = basePath[(lastSlash + 1)..];
        return segment.Length > 1
            && (segment[0] == 'v' || segment[0] == 'V')
            && segment[1..].All(char.IsDigit);
    }

    private static string Combine(string basePath, string suffix)
    {
        if (string.IsNullOrWhiteSpace(basePath) || basePath == "/")
            return "/" + suffix.TrimStart('/');

        return basePath + "/" + suffix.TrimStart('/');
    }
}
