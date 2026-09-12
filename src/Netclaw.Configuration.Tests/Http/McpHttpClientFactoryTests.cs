// -----------------------------------------------------------------------
// <copyright file="McpHttpClientFactoryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using Netclaw.Configuration.Http;
using Xunit;

namespace Netclaw.Configuration.Tests.Http;

public sealed class McpHttpClientFactoryTests
{
    [Fact]
    public async Task Initialize_removes_stale_protocol_version()
    {
        var captured = await SendAsync("initialize");

        Assert.Null(captured.ProtocolVersion);
        Assert.Equal("Bearer test-token", captured.Authorization);
    }

    [Theory]
    [InlineData("server/discover")]
    [InlineData("tools/list")]
    public async Task Other_methods_preserve_protocol_version(string method)
    {
        var captured = await SendAsync(method);

        Assert.Equal("2026-07-28", captured.ProtocolVersion);
    }

    private static async Task<CapturedHeaders> SendAsync(string method)
    {
        var capture = new CapturingHandler();
        using var client = McpHttpClientFactory.Create(capture);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/mcp");
        request.Headers.Add(McpHttpClientFactory.MethodHeaderName, method);
        request.Headers.Add(McpHttpClientFactory.ProtocolVersionHeaderName, "2026-07-28");
        request.Headers.Authorization = new("Bearer", "test-token");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return Assert.IsType<CapturedHeaders>(capture.Headers);
    }

    private sealed record CapturedHeaders(string? ProtocolVersion, string? Authorization);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public CapturedHeaders? Headers { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Headers = new(
                request.Headers.TryGetValues(
                    McpHttpClientFactory.ProtocolVersionHeaderName,
                    out var versions)
                    ? Assert.Single(versions)
                    : null,
                request.Headers.Authorization?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        }
    }
}
