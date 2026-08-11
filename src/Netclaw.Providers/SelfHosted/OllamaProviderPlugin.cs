// -----------------------------------------------------------------------
// <copyright file="OllamaProviderPlugin.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Configuration.Providers;
using Netclaw.Providers;
using OllamaSharp;

namespace Netclaw.Providers.SelfHosted;

/// <summary>
/// Daemon-side plugin for Ollama. Wraps <see cref="OllamaDescriptor"/>
/// and adds SDK client construction.
/// </summary>
public sealed class OllamaProviderPlugin : ProviderPluginBase<OllamaDescriptor>
{
    public OllamaProviderPlugin(OllamaDescriptor descriptor) : base(descriptor) { }

    public override IChatClient CreateChatClient(ProviderEntry entry, ModelReference model)
    {
        var endpoint = string.IsNullOrWhiteSpace(entry.Endpoint)
            ? new Uri(DefaultEndpoint)
            : new Uri(entry.Endpoint);
        return new OllamaApiClient(CreateLlmHttpClient(endpoint), model.ModelId);
    }

    public override IVendorOptionsSource? CreateVendorOptionsSource(ProviderEntry entry)
    {
        var vendorOptions = entry.GetVendorOptions<OllamaVendorOptions>() ?? new OllamaVendorOptions();
        return vendorOptions.DisableThinking ? new OllamaVendorOptionsSource() : null;
    }

    // OllamaSharp's request mapping consumes a top-level "think" field.
    public override ReasoningSuppressionDialect SuppressionDialect => ReasoningSuppressionDialect.OllamaThink;
}

internal sealed class OllamaVendorOptions : IVendorOptions
{
    public bool DisableThinking { get; set; }
}

internal sealed class OllamaVendorOptionsSource : IVendorOptionsSource
{
    public void Apply(ChatOptions options)
    {
        options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        options.AdditionalProperties["think"] = false;
    }
}
