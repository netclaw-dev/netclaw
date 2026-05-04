// -----------------------------------------------------------------------
// <copyright file="ContextWindowDoctorProbe.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Cli.Provider;
using Netclaw.Configuration;
using Netclaw.Providers.SelfHosted;

namespace Netclaw.Cli.Doctor;

internal static class ContextWindowDoctorProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public static async Task<int?> ProbeAsync(
        NetclawPaths paths, string modelId, string providerName, CancellationToken ct)
    {
        var providers = ProviderCommand.LoadProviders(paths);
        if (!providers.TryGetValue(providerName, out var entry))
            return null;

        using var httpClient = new HttpClient { Timeout = ProbeTimeout };

        IModelCapabilityResolver? resolver = entry.Type.ToLowerInvariant() switch
        {
            "ollama" => new OllamaCapabilityResolver(
                httpClient,
                NullLogger<OllamaCapabilityResolver>.Instance,
                entry.Endpoint),
            "openai-compatible" => new OpenAiCompatibleCapabilityResolver(
                httpClient,
                NullLogger<OpenAiCompatibleCapabilityResolver>.Instance,
                entry.Endpoint,
                entry.ApiKey?.Value),
            _ => null
        };

        if (resolver is null)
            return null;

        var result = await resolver.ResolveAsync(modelId, ct);
        return result?.ContextWindowTokens;
    }
}
