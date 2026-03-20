using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Mcp;
using Netclaw.Providers.OAuth;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

public sealed class McpOAuthServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-mcp-oauth-{Guid.NewGuid():N}");

    [Fact]
    public async Task GetFlowStatusByState_ReauthWithExistingToken_RemainsPending()
    {
        var service = CreateService(
            CreateDiscoveryClient(),
            CreatePkceService(JsonResponse(new
            {
                access_token = "access-token",
                refresh_token = "refresh-token",
                expires_in = 3600
            })));

        var entry = CreateHttpEntry();

        var (_, initialState) = await service.StartAuthorizationFlowAsync("textforge", entry, CancellationToken.None);
        await service.CompleteAuthorizationAsync("first-code", initialState, CancellationToken.None);

        var (_, reauthState) = await service.StartAuthorizationFlowAsync("textforge", entry, CancellationToken.None);

        Assert.Equal(McpOAuthFlowStatus.Pending, service.GetFlowStatusByState(reauthState));
        Assert.Equal(McpOAuthFlowStatus.Pending, service.GetFlowStatus("textforge"));
    }

    [Fact]
    public async Task GetFlowStatusByState_WhenTokenExchangeFails_ReturnsFailed()
    {
        var service = CreateService(
            CreateDiscoveryClient(),
            CreatePkceService(JsonResponse(new { error = "invalid_request" }, HttpStatusCode.BadRequest)));

        var (_, state) = await service.StartAuthorizationFlowAsync("textforge", CreateHttpEntry(), CancellationToken.None);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.CompleteAuthorizationAsync("bad-code", state, CancellationToken.None));

        Assert.Equal(McpOAuthFlowStatus.Failed, service.GetFlowStatusByState(state));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private McpOAuthService CreateService(HttpClient discoveryClient, OAuthPkceService pkceService)
    {
        return new McpOAuthService(
            discoveryClient,
            new NetclawPaths(_tempDir),
            TimeProvider.System,
            NullLogger<McpOAuthService>.Instance,
            pkceService,
            NullNotificationSink.Instance);
    }

    private static McpServerEntry CreateHttpEntry()
    {
        return new McpServerEntry
        {
            Transport = "http",
            Url = "https://mcp.example.com",
            OAuthClientId = "test-client"
        };
    }

    private static HttpClient CreateDiscoveryClient()
    {
        return new HttpClient(new FakeHttpMessageHandler(request => request.RequestUri!.ToString() switch
        {
            "https://mcp.example.com/" or "https://mcp.example.com" => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            "https://mcp.example.com/.well-known/oauth-protected-resource" => JsonResponse(new
            {
                authorization_servers = new[] { "https://auth.example.com" },
                resource = "https://mcp.example.com/resource"
            }),
            "https://auth.example.com/.well-known/oauth-authorization-server" => JsonResponse(new
            {
                authorization_endpoint = "https://auth.example.com/authorize",
                token_endpoint = "https://auth.example.com/token"
            }),
            _ => throw new InvalidOperationException($"Unexpected request URI: {request.RequestUri}")
        }));
    }

    private static OAuthPkceService CreatePkceService(HttpResponseMessage tokenResponse)
    {
        return new OAuthPkceService(new HttpClient(new FakeHttpMessageHandler(request => request.RequestUri!.ToString() switch
        {
            "https://auth.example.com/token" => tokenResponse,
            _ => throw new InvalidOperationException($"Unexpected request URI: {request.RequestUri}")
        })));
    }

    private static HttpResponseMessage JsonResponse(object body, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_handler(request));
        }
    }
}
