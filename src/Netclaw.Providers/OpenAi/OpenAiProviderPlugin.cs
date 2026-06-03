// -----------------------------------------------------------------------
// <copyright file="OpenAiProviderPlugin.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Providers;
using OpenAI;

namespace Netclaw.Providers.OpenAi;

/// <summary>
/// Daemon-side plugin for OpenAI. Handles both API key and OAuth (Codex) authentication.
/// OAuth tokens route to the Codex backend; API keys use the standard endpoint.
/// </summary>
public sealed class OpenAiProviderPlugin : ProviderPluginBase<OpenAiDescriptor>
{
    public OpenAiProviderPlugin(OpenAiDescriptor descriptor) : base(descriptor) { }

    public override IChatClient CreateChatClient(ProviderEntry entry, ModelReference model)
    {
        if (entry.AuthMethod is AuthMethod.OAuthPkce or AuthMethod.OAuthDevice)
        {
            // OAuth path → Codex backend
            var token = entry.OAuthAccessToken.RequireValid(
                "OpenAI OAuth access token (run 'netclaw provider fix <name>')");

            var accountId = JwtAccountIdExtractor.ResolveAccountId(entry)
                ?? throw new InvalidOperationException(
                    "OpenAI OAuth credential is missing ChatGPT account ID. Re-authenticate with 'netclaw provider fix <name>'.");
            var options = new OpenAIClientOptions
            {
                Endpoint = new Uri("https://chatgpt.com/backend-api/codex")
            };
            options.AddPolicy(new OpenAiCodexRequestPolicy(accountId), PipelinePosition.PerCall);

            // The Codex backend rejects non-streaming Responses calls with
            // 400 {"detail":"Stream must be set to true"}. Netclaw's session loop
            // streams, but auxiliary calls (title generation, memory extraction,
            // compaction) use the non-streaming GetResponseAsync path. Wrap the
            // client so those calls are served by streaming under the hood.
            return new StreamingOnlyChatClient(
                new OpenAI.Responses.ResponsesClient(
                        new ApiKeyCredential(token.Value), options)
                    .AsIChatClient(model.ModelId));
        }

        // API key path → standard endpoint
        var apiKey = GetRequiredApiKey(entry, TypeKey);
        return new OpenAI.Responses.ResponsesClient(apiKey)
            .AsIChatClient(model.ModelId);
    }
}

/// <summary>
/// Serves non-streaming <see cref="IChatClient.GetResponseAsync"/> calls by consuming the
/// underlying streaming endpoint and aggregating the updates. Required for the OpenAI Codex
/// backend, which rejects non-streaming Responses requests with
/// <c>400 {"detail":"Stream must be set to true"}</c>. Streaming calls pass straight through.
/// </summary>
internal sealed class StreamingOnlyChatClient : DelegatingChatClient
{
    public StreamingOnlyChatClient(IChatClient innerClient) : base(innerClient) { }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(messages, options, cancellationToken)
            .ToChatResponseAsync(cancellationToken);
}
