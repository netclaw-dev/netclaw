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
using OpenAI.Responses;

namespace Netclaw.Providers.GitHubCopilot;

/// <summary>
/// Daemon-side plugin for GitHub Copilot. Routes chat completions through the
/// OpenAI SDK, with <see cref="CopilotRequestPolicy"/> handling token refresh,
/// the three Copilot-specific headers, and directing the request to the host the
/// token is valid at (<c>endpoints.api</c> — the public host for standard
/// accounts, a tenant host for GHE data residency).
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
        return new GitHubCopilotCapabilityResolvingChatClient(async () =>
        {
            var capability = await Descriptor.ResolveModelCapabilityAsync(entry, model.ModelId)
                .ConfigureAwait(false);
            return CreateSdkClient(entry, model.Provider, capability);
        });
    }

    private IChatClient CreateSdkClient(
        ProviderEntry entry, string? providerName, GitHubCopilotModelCapability capability)
    {
        var endpoint = string.IsNullOrWhiteSpace(entry.Endpoint)
            ? Descriptor.DefaultEndpoint
            : entry.Endpoint.TrimEnd('/');
        var credential = new ApiKeyCredential("placeholder");
        var oauth = GitHubCopilotDescriptor.CreateOAuthAuth(entry);
        var followTokenHost = !GitHubCopilotDescriptor.HasCustomEndpointOverride(entry.Endpoint);

        if (capability.PreferredApi is null)
        {
            var advertised = capability.SupportedEndpoints.Count == 0
                ? "(none)"
                : string.Join(", ", capability.SupportedEndpoints);
            throw new InvalidOperationException(
                $"GitHub Copilot model '{capability.ModelId}' does not advertise a supported HTTP inference endpoint. "
                + $"Advertised endpoints: {advertised}.");
        }

        return capability.PreferredApi == GitHubCopilotApiKind.Responses
            ? CreateResponsesClient()
            : CreateChatCompletionsClient();

        IChatClient CreateResponsesClient()
        {
            var options = new ResponsesClientOptions { Endpoint = new Uri(endpoint) };
            if (TransportOverride is not null)
                options.Transport = TransportOverride;
            options.AddPolicy(CreateRequestPolicy(), PipelinePosition.PerCall);
            return new ResponsesClient(credential, options).AsIChatClient(capability.ModelId);
        }

        IChatClient CreateChatCompletionsClient()
        {
            var options = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };
            if (TransportOverride is not null)
                options.Transport = TransportOverride;
            options.AddPolicy(CreateRequestPolicy(), PipelinePosition.PerCall);
            return new OpenAIClient(credential, options)
                .GetChatClient(capability.ModelId)
                .AsIChatClient();
        }

        CopilotRequestPolicy CreateRequestPolicy() =>
            new(tokenExchanger, entry, credential, followTokenHost, providerName, oauth);
    }
}
