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
using Netclaw.Providers.OAuth;
using OpenAI;

namespace Netclaw.Providers.OpenAi;

/// <summary>
/// Daemon-side plugin for OpenAI. Handles both API key and OAuth (Codex) authentication.
/// OAuth tokens route to the Codex backend; API keys use the standard endpoint.
/// </summary>
public sealed class OpenAiProviderPlugin : ProviderPluginBase<OpenAiDescriptor>
{
    private readonly ProviderOAuthTokenRefreshService? _tokenRefreshService;

    public OpenAiProviderPlugin(
        OpenAiDescriptor descriptor,
        ProviderOAuthTokenRefreshService? tokenRefreshService = null) : base(descriptor)
    {
        _tokenRefreshService = tokenRefreshService;
    }

    public override IChatClient CreateChatClient(ProviderEntry entry, ModelReference model)
    {
        if (entry.AuthMethod is AuthMethod.OAuthPkce or AuthMethod.OAuthDevice)
        {
            // OAuth path → Codex backend
            var token = entry.OAuthAccessToken.RequireValid(
                "OpenAI OAuth access token (run 'netclaw provider fix <name>')");

            var accountId = JwtAccountIdExtractor.ResolveAccountId(entry);
            if (_tokenRefreshService is null && accountId is null)
            {
                throw new InvalidOperationException(
                    "OpenAI OAuth credential is missing ChatGPT account ID. Re-authenticate with 'netclaw provider fix <name>'.");
            }
            var oauth = Descriptor.Auth.GetOAuthConfig()
                        ?? throw new InvalidOperationException("OpenAI OAuth configuration is unavailable.");
            var options = new OpenAI.Responses.ResponsesClientOptions
            {
                Endpoint = new Uri("https://chatgpt.com/backend-api/codex")
            };
            var credential = new ApiKeyCredential(token.Value);
            options.AddPolicy(
                _tokenRefreshService is null
                    ? new OpenAiCodexRequestPolicy(accountId!)
                    : new OpenAiCodexRequestPolicy(
                        model.Provider,
                        entry,
                        oauth,
                        credential,
                        _tokenRefreshService),
                PipelinePosition.PerCall);

            // No non-streaming wrapper is needed here: Netclaw issues streaming-only
            // LLM calls everywhere (the session loop and every auxiliary caller —
            // title generation, memory extraction, compaction — go through the
            // streaming transport), so the Codex backend's
            // 400 {"detail":"Stream must be set to true"} on non-streaming Responses
            // calls is structurally unreachable.
            return new OpenAI.Responses.ResponsesClient(
                    credential, options)
                .AsIChatClient(model.ModelId);
        }

        // API key path → standard endpoint
        var apiKey = GetRequiredApiKey(entry, TypeKey);
        return new OpenAI.Responses.ResponsesClient(apiKey)
            .AsIChatClient(model.ModelId);
    }
}
