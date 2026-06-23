// -----------------------------------------------------------------------
// <copyright file="CopilotRequestPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ClientModel;
using System.ClientModel.Primitives;
using Netclaw.Configuration;

namespace Netclaw.Providers.GitHubCopilot;

/// <summary>
/// Pipeline policy for <c>api.githubcopilot.com</c>. On every outbound
/// request it resolves a fresh Copilot API token via
/// <see cref="CopilotTokenExchanger"/> (cached, with a 2-minute refresh
/// buffer) and adds the three custom headers the Copilot API rejects
/// requests without.
/// </summary>
/// <remarks>
/// The Authorization header is NOT set here. The OpenAI SDK's own
/// key-credential auth policy runs after every policy we can register and
/// writes <c>Authorization: Bearer {key}</c> from <paramref name="credential"/>
/// at send time — so anything we set on the header is overwritten. Instead we
/// refresh the credential's value via <see cref="ApiKeyCredential.Update"/>
/// before the auth policy reads it, so the SDK emits our short-lived Copilot
/// token. (Setting the header directly produced
/// "bad request: Authorization header is badly formatted" because the SDK
/// replaced our token with the placeholder credential.)
///
/// See <c>openspec/changes/fix-github-copilot-auth-header/design.md</c>
/// § References for the System.ClientModel / OpenAI SDK contract this relies
/// on (PipelinePosition layering, where the SDK plants its auth policy, and
/// the documented use of <see cref="ApiKeyCredential.Update"/>).
/// </remarks>
internal sealed class CopilotRequestPolicy(
    CopilotTokenExchanger exchanger,
    ProviderEntry entry,
    ApiKeyCredential credential,
    string? providerName = null,
    OAuthAuth? oauth = null)
    : PipelinePolicy
{
    public override void Process(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        // The OpenAI SDK's chat-completions path always invokes ProcessAsync.
        // The sync overload exists on PipelinePolicy because the abstract
        // contract requires it, but in practice it's only hit by callers
        // that misconfigure their client. We refuse to block on an async
        // network call (deadlock risk under a sync context, synchronous I/O
        // on the calling thread) and fail loudly per the no-silent-fallback
        // rule in CLAUDE.md.
        throw new NotSupportedException(
            "CopilotRequestPolicy requires the async pipeline. Token exchange " +
            "is an async network call; use the OpenAI SDK's async chat methods " +
            "(e.g. ChatClient.CompleteChatAsync) rather than the synchronous overloads.");
    }

    public override async ValueTask ProcessAsync(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        var token = providerName is not null && oauth is not null
            ? await exchanger.GetTokenAsync(providerName, entry, oauth, message.CancellationToken)
            : await exchanger.GetTokenAsync(entry, message.CancellationToken);

        // Refresh the credential the SDK's auth policy reads downstream, so it
        // emits "Authorization: Bearer {token}" with our short-lived token.
        credential.Update(token);
        ApplyCopilotHeaders(message);
        await ProcessNextAsync(message, pipeline, currentIndex);
    }

    private static void ApplyCopilotHeaders(PipelineMessage message)
    {
        var headers = message.Request.Headers;

        // The Copilot API validates copilot-integration-id against a closed
        // set and rejects unknown values. "vscode-chat" is the closest
        // registered identifier we can use until we register a Netclaw
        // integration with GitHub.
        headers.Set("copilot-integration-id", "vscode-chat");
        headers.Set("editor-version", "Netclaw/1.0");
        headers.Set("openai-intent", "conversation-agent");
    }
}
