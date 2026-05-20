// -----------------------------------------------------------------------
// <copyright file="GitHubCopilotProviderPlugin.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using OpenAI;

namespace Netclaw.Providers.GitHubCopilot;

/// <summary>
/// Daemon-side plugin for GitHub Copilot. Routes chat completions through
/// the OpenAI SDK pointed at <c>api.githubcopilot.com/chat/completions</c>
/// with <see cref="CopilotRequestPolicy"/> handling token refresh and the
/// three Copilot-specific headers.
/// </summary>
public sealed class GitHubCopilotProviderPlugin(
    GitHubCopilotDescriptor descriptor,
    CopilotTokenExchanger tokenExchanger)
    : ProviderPluginBase<GitHubCopilotDescriptor>(descriptor)
{
    public override IChatClient CreateChatClient(ProviderEntry entry, ModelReference model)
    {
        var endpoint = string.IsNullOrWhiteSpace(entry.Endpoint)
            ? Descriptor.DefaultEndpoint
            : entry.Endpoint.TrimEnd('/');

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint),
        };

        // The policy below overwrites the Authorization header on every
        // call with a fresh Copilot API token, so the credential we pass
        // to the SDK is a placeholder.
        options.AddPolicy(
            new CopilotRequestPolicy(tokenExchanger, entry),
            PipelinePosition.PerCall);

        var client = new OpenAIClient(new ApiKeyCredential("placeholder"), options);
        return client.GetChatClient(model.ModelId).AsIChatClient();
    }
}
