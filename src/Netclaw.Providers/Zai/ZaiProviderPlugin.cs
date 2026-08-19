// -----------------------------------------------------------------------
// <copyright file="ZaiProviderPlugin.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Providers.SelfHosted;

namespace Netclaw.Providers.Zai;

/// <summary>
/// Daemon-side plugin for Z.ai's hosted API.
/// </summary>
public sealed class ZaiProviderPlugin(
    ZaiDescriptor descriptor,
    ILoggerFactory loggerFactory) : ProviderPluginBase<ZaiDescriptor>(descriptor)
{
    public override IChatClient CreateChatClient(ProviderEntry entry, ModelReference model)
    {
        var apiKey = entry.ApiKey?.Value;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"Provider type '{TypeKey}' requires an API key. Configure ApiKey in secrets.json.");
        }

        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl(
            string.IsNullOrWhiteSpace(entry.Endpoint) ? DefaultEndpoint : entry.Endpoint,
            apiKey);

        return new OpenAiCompatibleChatClient(
            CreateLlmHttpClient(endpoint.BaseUri),
            endpoint,
            model.ModelId,
            OpenAiCompatibleWireProfile.Zai,
            loggerFactory.CreateLogger<OpenAiCompatibleChatClient>());
    }

    public override ReasoningSuppressionDialect SuppressionDialect =>
        ReasoningSuppressionDialect.ZaiThinking;
}
