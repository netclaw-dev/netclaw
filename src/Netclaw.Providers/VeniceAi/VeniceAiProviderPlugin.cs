// -----------------------------------------------------------------------
// <copyright file="VeniceAiProviderPlugin.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Configuration.Providers;
using OpenAI;
using OpenAI.Chat;

namespace Netclaw.Providers.VeniceAi;

/// <summary>
/// Daemon-side plugin for Venice.ai. Wraps <see cref="VeniceAiDescriptor"/>
/// and constructs an OpenAI-compatible chat client against Venice's
/// <c>/api/v1</c> endpoint. Attaches <see cref="VeniceAiSystemPromptOverridePolicy"/>
/// unless the operator has explicitly opted in to Venice's system prompt.
/// </summary>
public sealed class VeniceAiProviderPlugin : ProviderPluginBase<VeniceAiDescriptor>
{
    public VeniceAiProviderPlugin(VeniceAiDescriptor descriptor) : base(descriptor) { }

    public override IChatClient CreateChatClient(ProviderEntry entry, ModelReference model)
    {
        var apiKey = GetRequiredApiKey(entry, TypeKey);
        var endpoint = string.IsNullOrWhiteSpace(entry.Endpoint)
            ? new Uri(DefaultEndpoint)
            : new Uri(entry.Endpoint);
        var vendorOptions = entry.GetVendorOptions<VeniceAiVendorOptions>() ?? new VeniceAiVendorOptions();

        var options = new OpenAIClientOptions { Endpoint = endpoint };
        if (CreateTransportOverride() is { } transport)
            options.Transport = transport;
        if (!vendorOptions.IncludeVeniceSystemPrompt)
            options.AddPolicy(new VeniceAiSystemPromptOverridePolicy(), PipelinePosition.PerCall);

        var client = new OpenAIClient(new ApiKeyCredential(apiKey), options);

        return new MaxEffortChatClient(
            client.GetChatClient(model.ModelId).AsIChatClient(),
            _ => new ChatCompletionOptions { ReasoningEffortLevel = new ChatReasoningEffortLevel("max") });
    }
}
