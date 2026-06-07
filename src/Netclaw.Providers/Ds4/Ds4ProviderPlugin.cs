// -----------------------------------------------------------------------
// <copyright file="Ds4ProviderPlugin.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Providers.SelfHosted;

namespace Netclaw.Providers.Ds4;

/// <summary>
/// Daemon-side plugin for DwarfStar (ds4). Builds chat clients against ds4's
/// OpenAI-compatible endpoint via <see cref="OpenAiCompatibleChatClient"/> —
/// the self-hosted chat path that surfaces server-side prefill/decode timings
/// and tolerates self-hosted response quirks. ds4 emits native OpenAI
/// <c>tool_calls</c>, so no text tool-call fallback is needed.
/// </summary>
public sealed class Ds4ProviderPlugin : ProviderPluginBase<Ds4Descriptor>
{
    private readonly ILoggerFactory _loggerFactory;

    public Ds4ProviderPlugin(Ds4Descriptor descriptor, ILoggerFactory loggerFactory)
        : base(descriptor)
    {
        _loggerFactory = loggerFactory;
    }

    public override IChatClient CreateChatClient(ProviderEntry entry, ModelReference model)
    {
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl(
            string.IsNullOrWhiteSpace(entry.Endpoint) ? DefaultEndpoint : entry.Endpoint,
            entry.ApiKey?.Value);

        return new OpenAiCompatibleChatClient(
            CreateLlmHttpClient(endpoint.BaseUri),
            endpoint,
            model.ModelId,
            _loggerFactory.CreateLogger<OpenAiCompatibleChatClient>());
    }
}
