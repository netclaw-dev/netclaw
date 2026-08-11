// -----------------------------------------------------------------------
// <copyright file="McpHttpClientFactory.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;

namespace Netclaw.Configuration.Http;

/// <summary>
/// Owns the process-wide HTTP client for MCP transports.
/// </summary>
internal static class McpHttpClientFactory
{
    internal const string MethodHeaderName = "Mcp-Method";
    internal const string ProtocolVersionHeaderName = "MCP-Protocol-Version";
    internal const string InitializeMethod = "initialize";

    /// <summary>
    /// Gets the client shared by every MCP HTTP transport in this process.
    /// The process owns its lifetime so one short-lived transport cannot close
    /// the connection pool while another transport is using it.
    /// </summary>
    public static HttpClient Shared { get; } = Create(new SocketsHttpHandler
    {
        // MCP profiles share this connection pool. Ambient cookies could cross
        // profile boundaries on the same host; authentication stays explicit.
        UseCookies = false,
    });

    internal static HttpClient Create(HttpMessageHandler innerHandler)
    {
        ArgumentNullException.ThrowIfNull(innerHandler);

        // Order matters: the rejection handler must see the token endpoint's final
        // response, so it wraps the protocol-version handler rather than the reverse.
        return new HttpClient(new OAuthClientRejectionHandler
        {
            InnerHandler = new ProtocolVersionHandler
            {
                InnerHandler = innerHandler,
            },
        });
    }

    /// <summary>
    /// Removes stale MCP protocol state from an HTTP initialize request.
    /// </summary>
    /// <remarks>
    /// MCP SDK 2.x can retain the protocol version from a completed discovery
    /// request when its discovery wait is cancelled. The SDK can then send an
    /// initialize body for the legacy protocol with the retained modern
    /// protocol version in the HTTP header. An initialize request starts
    /// negotiation, so it must not carry state from the discovery attempt.
    /// </remarks>
    private sealed class ProtocolVersionHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Headers.TryGetValues(MethodHeaderName, out var methods)
                && methods.Contains(InitializeMethod, StringComparer.Ordinal))
            {
                request.Headers.Remove(ProtocolVersionHeaderName);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}

/// <summary>
/// Surfaces an OAuth <c>invalid_client</c> rejection from the token endpoint as a distinct
/// exception the MCP SDK does not recognize.
/// </summary>
/// <remarks>
/// MCP SDK 2.1 probes the server with <c>server/discover</c> before the initialize
/// handshake. When the authorization-code exchange for that probe fails with
/// <c>400 invalid_client</c>, the SDK reads the 400 as an unsupported-protocol signal,
/// falls back to the initialize handshake, and calls the one-shot authorization callback a
/// second time. That second call fails as "authorization already in progress" and hides the
/// real reason the exchange failed, so the manager can no longer tell a dead dynamic client
/// registration from any other connection failure. This handler reads the token error before
/// the SDK sees the 400 and throws a type the discover fallback does not catch, so the true
/// cause reaches the manager. The exchange is doomed either way; the throw only replaces the
/// misleading failure with an accurate one.
/// </remarks>
internal sealed class OAuthClientRejectionHandler : DelegatingHandler
{
    private const string FormContentType = "application/x-www-form-urlencoded";
    private const string AuthorizationCodeGrant = "grant_type=authorization_code";
    private const string InvalidClientError = "invalid_client";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Only the OAuth token endpoint sends form-urlencoded bodies; MCP JSON-RPC traffic is
        // JSON. Skip the body read for everything else so tool-call payloads stay untouched.
        if (request.Method != HttpMethod.Post
            || request.Content?.Headers.ContentType?.MediaType != FormContentType)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var requestBody = await request.Content.ReadAsStringAsync(cancellationToken);
        var response = await base.SendAsync(request, cancellationToken);

        // Only the authorization-code exchange drives client-identity discard. A refresh
        // failure keeps the SDK's graceful null path, so it is deliberately left untouched.
        if (response.StatusCode is HttpStatusCode.BadRequest
            && requestBody.Contains(AuthorizationCodeGrant, StringComparison.Ordinal)
            && await IsInvalidClientErrorAsync(response))
        {
            response.Dispose();
            throw new McpOAuthClientRejectedException();
        }

        return response;
    }

    private static async Task<bool> IsInvalidClientErrorAsync(HttpResponseMessage response)
    {
        // Buffer the small error body so the check does not consume the stream the caller
        // would read on the pass-through path.
        await response.Content.LoadIntoBufferAsync();
        var body = await response.Content.ReadAsStringAsync();
        return body.Contains(InvalidClientError, StringComparison.Ordinal);
    }
}

/// <summary>
/// Signals that the OAuth token endpoint rejected the client registration with
/// <c>invalid_client</c>. The message carries the OAuth error code so the manager's
/// message-based auth-failure checks recognize it.
/// </summary>
internal sealed class McpOAuthClientRejectedException()
    : Exception(
        "The OAuth token endpoint rejected the client registration (invalid_client). " +
        "The dynamic client identity is no longer valid and must be discarded.");
