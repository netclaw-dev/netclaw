// -----------------------------------------------------------------------
// <copyright file="OpenRouterProviderPlugin.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Configuration.Providers;
using Netclaw.Providers;
using OpenAI;
using OpenAI.Chat;

namespace Netclaw.Providers.OpenRouter;

/// <summary>
/// Daemon-side plugin for OpenRouter. Wraps <see cref="OpenRouterDescriptor"/>
/// and adds SDK client construction with reasoning-exclude pipeline policy.
/// </summary>
public sealed class OpenRouterProviderPlugin : ProviderPluginBase<OpenRouterDescriptor>
{
    public OpenRouterProviderPlugin(OpenRouterDescriptor descriptor) : base(descriptor) { }

    public override IChatClient CreateChatClient(ProviderEntry entry, ModelReference model)
    {
        var apiKey = GetRequiredApiKey(entry, TypeKey);
        var endpoint = string.IsNullOrWhiteSpace(entry.Endpoint)
            ? new Uri(DefaultEndpoint)
            : new Uri(entry.Endpoint);
        var vendorOptions = entry.GetVendorOptions<OpenRouterVendorOptions>() ?? new OpenRouterVendorOptions();

        var options = new OpenAIClientOptions { Endpoint = endpoint };
        if (CreateTransportOverride() is { } transport)
            options.Transport = transport;
        if (vendorOptions.ExcludeReasoning)
            options.AddPolicy(new OpenRouterReasoningExcludePolicy(), PipelinePosition.PerCall);

        var client = new OpenAIClient(new ApiKeyCredential(apiKey), options);

        return new MaxEffortChatClient(
            client.GetChatClient(model.ModelId).AsIChatClient(),
            _ => new ChatCompletionOptions { ReasoningEffortLevel = new ChatReasoningEffortLevel("max") });
    }

    public override IVendorOptionsSource? CreateVendorOptionsSource(ProviderEntry entry)
    {
        var vendorOptions = entry.GetVendorOptions<OpenRouterVendorOptions>() ?? new OpenRouterVendorOptions();
        return vendorOptions.ExcludeReasoning ? new OpenRouterVendorOptionsSource() : null;
    }
}
