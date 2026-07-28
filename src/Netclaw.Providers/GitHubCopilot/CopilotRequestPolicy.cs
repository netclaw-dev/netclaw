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
/// Pipeline policy for the Copilot chat API. On every outbound request it
/// resolves a fresh Copilot API token via <see cref="CopilotTokenExchanger"/>
/// (cached, with a 2-minute refresh buffer), routes the request to the host the
/// token is valid at (the exchange's <c>endpoints.api</c>), and adds the three
/// custom headers the Copilot API rejects requests without.
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
    bool followTokenHost,
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
        credential.Update(token.Token.Value);
        RouteToCopilotApiHost(message, token.ApiBase);
        ApplyCopilotHeaders(message);
        await ProcessNextAsync(message, pipeline, currentIndex);
    }

    // The chat host must follow the token, not the statically-configured provider
    // endpoint. The token exchange reports (in endpoints.api) the host its token
    // is valid at; for GitHub Enterprise Cloud data residency that's a
    // tenant-specific api.<subdomain>.ghe.com, and a token minted there is
    // rejected with HTTP 400 at the public api.githubcopilot.com the OpenAI SDK
    // client is otherwise configured with (issue #1550). We rebase onto the
    // token's origin AND any base path it carries, then re-append the SDK-built
    // path and query (/chat/completions?api-version=...). endpoints.api is a bare
    // origin today, so the base path is normally empty — but prepending it keeps
    // this consistent with the probe (which appends /models to the same base),
    // so both paths agree if GitHub ever returns a path-bearing endpoints.api.
    //
    // followTokenHost is false when the operator deliberately pointed the entry
    // at a custom host (e.g. a corporate proxy); we respect that override and
    // never silently reroute their traffic to the token's host.
    private void RouteToCopilotApiHost(PipelineMessage message, Uri? apiBase)
    {
        // Operator pinned a custom endpoint — use it verbatim, ignore the token's host.
        if (!followTokenHost)
            return;

        // We are meant to follow the token's host but the exchange reported none
        // (missing or non-HTTPS endpoints.api). Do NOT guess a host — sending an
        // auth token to a host it may not be valid at is exactly issue #1550. Fail
        // loudly rather than silently fall back to the configured default.
        if (apiBase is null)
            throw new InvalidOperationException(
                "GitHub Copilot token exchange did not return a usable API host "
                + "(endpoints.api). Refusing to route chat/completions to a guessed "
                + "host — re-authenticate the provider, or set an explicit endpoint "
                + "to override the Copilot API host.");

        if (message.Request.Uri is not { } current)
            return;

        var basePath = apiBase.AbsolutePath.TrimEnd('/');
        message.Request.Uri = new UriBuilder(current)
        {
            Scheme = apiBase.Scheme,
            Host = apiBase.Host,
            Port = apiBase.IsDefaultPort ? -1 : apiBase.Port,
            Path = basePath + current.AbsolutePath,
        }.Uri;
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
        headers.Set("user-agent", NetclawUserAgent.Value);
    }
}
