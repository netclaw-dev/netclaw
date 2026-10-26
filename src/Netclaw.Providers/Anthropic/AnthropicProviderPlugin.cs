// -----------------------------------------------------------------------
// <copyright file="AnthropicProviderPlugin.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Providers;

namespace Netclaw.Providers.Anthropic;

/// <summary>
/// Daemon-side plugin for Anthropic. Wraps <see cref="AnthropicDescriptor"/>
/// and adds SDK client construction.
/// </summary>
public sealed class AnthropicProviderPlugin : ProviderPluginBase<AnthropicDescriptor>
{
    public AnthropicProviderPlugin(AnthropicDescriptor descriptor) : base(descriptor) { }

    public override IChatClient CreateChatClient(ProviderEntry entry, ModelReference model)
    {
        var apiKey = GetRequiredApiKey(entry, TypeKey);
        var client = new AnthropicClient(new()
        {
            ApiKey = apiKey,
            HttpClient = CreateLlmHttpClient()
        });
        return new MaxEffortChatClient(client.AsIChatClient(model.ModelId), options => new MessageCreateParams
        {
            // A raw request replaces the adapter's own max_tokens, so it carries the limit of the call.
            MaxTokens = options.MaxOutputTokens
                ?? throw new InvalidOperationException("An Anthropic call with thinking level 'max' needs an output limit."),
            Messages = [],
            Model = options.ModelId ?? model.ModelId,
            OutputConfig = new OutputConfig { Effort = Effort.Max },
        });
    }
}
