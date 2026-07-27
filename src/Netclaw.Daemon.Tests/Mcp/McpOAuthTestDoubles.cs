// -----------------------------------------------------------------------
// <copyright file="McpOAuthTestDoubles.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Daemon.Mcp;

namespace Netclaw.Daemon.Tests.Mcp;

internal static class McpOAuthTestDoubles
{
    /// <summary>
    /// A registrar for tests that never take the explicit-authorization path. Any HTTP
    /// call fails loudly, so a test that starts registering by accident reports it
    /// instead of quietly behaving as if the server had no OAuth metadata.
    /// </summary>
    public static McpOAuthClientRegistrar UnusedRegistrar()
        => new(new HttpClient(new UnreachableHandler()), NullLogger<McpOAuthClientRegistrar>.Instance);

    public static McpOAuthClientRegistrar RegistrarFor(HttpClient httpClient)
        => new(httpClient, NullLogger<McpOAuthClientRegistrar>.Instance);

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                $"This test's MCP OAuth registrar was not expected to issue requests (attempted {request.RequestUri}).");
    }
}
