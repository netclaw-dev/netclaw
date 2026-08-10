// -----------------------------------------------------------------------
// <copyright file="McpHttpClientFactory.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
        return new HttpClient(new ProtocolVersionHandler
        {
            InnerHandler = innerHandler,
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
