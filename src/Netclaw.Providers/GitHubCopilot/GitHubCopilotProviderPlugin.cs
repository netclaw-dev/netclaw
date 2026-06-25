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
    // Test seam: when set, routes the OpenAI SDK through this transport instead
    // of the real network, so a test can capture the fully-assembled outgoing
    // request — including the Authorization header the SDK's own credential
    // policy writes — and prove the exchanged token (not the placeholder)
    // reaches the wire. Never set in production wiring.
    internal PipelineTransport? TransportOverride { get; init; }

    public override IChatClient CreateChatClient(ProviderEntry entry, ModelReference model)
    {
        var authOptions = GitHubCopilotDescriptor.ResolveOptions(entry);
        var endpointOverride = GitHubCopilotDescriptor.NormalizeCopilotEndpointOverride(entry.Endpoint);
        var endpoint = string.IsNullOrWhiteSpace(endpointOverride)
            ? authOptions.CopilotApiBase.ToString().TrimEnd('/')
            : endpointOverride;

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint),
        };

        if (TransportOverride is not null)
            options.Transport = TransportOverride;

        // The SDK owns the Authorization header: its key-credential auth policy
        // runs after any policy we register and writes "Bearer {key}" from this
        // credential at send time. So we hand it a mutable credential and let
        // CopilotRequestPolicy refresh its value (to a fresh short-lived Copilot
        // token) on every call. The "placeholder" is overwritten before the
        // first request goes out.
        var credential = new ApiKeyCredential("placeholder");
        var oauth = GitHubCopilotDescriptor.CreateOAuthAuth(authOptions);
        options.AddPolicy(
            new CopilotRequestPolicy(tokenExchanger, entry, credential, model.Provider, oauth),
            PipelinePosition.PerCall);
        options.AddPolicy(new CopilotChatRequestSanitizerPolicy(), PipelinePosition.BeforeTransport);

        var client = new OpenAIClient(credential, options);
        return client.GetChatClient(model.ModelId).AsIChatClient();
    }
}

internal sealed class CopilotChatRequestSanitizerPolicy : PipelinePolicy
{
    public override void Process(
        PipelineMessage message,
        IReadOnlyList<PipelinePolicy> pipeline,
        int currentIndex)
    {
        RemoveRejectedHeaders(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override ValueTask ProcessAsync(
        PipelineMessage message,
        IReadOnlyList<PipelinePolicy> pipeline,
        int currentIndex)
    {
        RemoveRejectedHeaders(message);
        return ProcessNextAsync(message, pipeline, currentIndex);
    }

    private static void RemoveRejectedHeaders(PipelineMessage message)
    {
        message.Request.Headers.Remove("X-GitHub-Api-Version");
    }
}
