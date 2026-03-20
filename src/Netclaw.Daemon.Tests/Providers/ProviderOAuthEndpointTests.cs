using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Configuration;
using Netclaw.Daemon.Providers;
using Netclaw.Providers;
using Netclaw.Providers.OAuth;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class ProviderOAuthEndpointTests
{
    [Fact]
    public async Task StartEndpoint_ReturnsAuthorizationUrl_AndPendingStatus()
    {
        await using var host = await CreateHostAsync(_ => SuccessfulTokenResponse());
        var client = host.GetTestClient();

        var startResponse = await client.PostAsync("/api/provider/oauth/start?provider=test-oauth", null);
        startResponse.EnsureSuccessStatusCode();

        var startPayload = await startResponse.Content.ReadFromJsonAsync<JsonElement>();
        var state = startPayload.GetProperty("state").GetString();
        var authorizationUrl = startPayload.GetProperty("authorizationUrl").GetString();

        Assert.NotNull(state);
        Assert.NotNull(authorizationUrl);
        Assert.Contains("code_challenge=", authorizationUrl);
        Assert.Contains($"state={state}", authorizationUrl);

        var statusPayload = await client.GetFromJsonAsync<JsonElement>($"/api/provider/oauth/status/{state}");
        Assert.Equal("Pending", statusPayload.GetProperty("status").GetString());
        Assert.False(statusPayload.GetProperty("hasToken").GetBoolean());
    }

    [Fact]
    public async Task CallbackEndpoint_CompletesFlow_AndStatusReturnsTokens()
    {
        await using var host = await CreateHostAsync(_ => SuccessfulTokenResponse());
        var client = host.GetTestClient();

        var startResponse = await client.PostAsync("/api/provider/oauth/start?provider=test-oauth", null);
        var startPayload = await startResponse.Content.ReadFromJsonAsync<JsonElement>();
        var state = startPayload.GetProperty("state").GetString();

        var callbackResponse = await client.GetAsync($"/api/provider/oauth/callback?code=test-code&state={state}");
        callbackResponse.EnsureSuccessStatusCode();
        var html = await callbackResponse.Content.ReadAsStringAsync();
        Assert.Contains("Authorization complete", html);

        var statusPayload = await client.GetFromJsonAsync<JsonElement>($"/api/provider/oauth/status/{state}");
        Assert.Equal("Completed", statusPayload.GetProperty("status").GetString());
        Assert.True(statusPayload.GetProperty("hasToken").GetBoolean());
        Assert.Equal("access-token", statusPayload.GetProperty("accessToken").GetString());
        Assert.Equal("refresh-token", statusPayload.GetProperty("refreshToken").GetString());
    }

    [Fact]
    public async Task CallbackEndpoint_OnExchangeFailure_Returns500_AndFailedStatus()
    {
        await using var host = await CreateHostAsync(_ =>
            JsonResponse(new { error = "invalid_request" }, HttpStatusCode.BadRequest));
        var client = host.GetTestClient();

        var startResponse = await client.PostAsync("/api/provider/oauth/start?provider=test-oauth", null);
        var startPayload = await startResponse.Content.ReadFromJsonAsync<JsonElement>();
        var state = startPayload.GetProperty("state").GetString();

        var callbackResponse = await client.GetAsync($"/api/provider/oauth/callback?code=bad-code&state={state}");
        Assert.Equal(HttpStatusCode.InternalServerError, callbackResponse.StatusCode);
        var html = await callbackResponse.Content.ReadAsStringAsync();
        Assert.Contains("Authorization failed", html);

        var statusPayload = await client.GetFromJsonAsync<JsonElement>($"/api/provider/oauth/status/{state}");
        Assert.Equal("Failed", statusPayload.GetProperty("status").GetString());
        Assert.False(statusPayload.GetProperty("hasToken").GetBoolean());
    }

    private static async Task<WebApplication> CreateHostAsync(Func<HttpRequestMessage, HttpResponseMessage> tokenHandler)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddSingleton(new OAuthPkceService(new HttpClient(new FakeHttpMessageHandler(tokenHandler))));
        builder.Services.AddSingleton<IProviderOAuthCallbackListener, NoOpProviderOAuthCallbackListener>();
        builder.Services.AddSingleton(new ProviderDescriptorRegistry([new TestOAuthDescriptor()]));

        var app = builder.Build();
        app.MapProviderOAuthEndpoints();
        await app.StartAsync();
        return app;
    }

    private static HttpResponseMessage SuccessfulTokenResponse()
    {
        return JsonResponse(new
        {
            access_token = "access-token",
            refresh_token = "refresh-token",
            expires_in = 3600
        });
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

    private sealed class NoOpProviderOAuthCallbackListener : IProviderOAuthCallbackListener
    {
        public void StartListening(string redirectUri, string state)
        {
        }
    }

    private sealed class TestOAuthDescriptor : IProviderDescriptor
    {
        public string TypeKey => "test-oauth";
        public string DisplayName => "Test OAuth";
        public string DefaultEndpoint => "https://api.example.com";
        public string ModelListingPath => "/v1/models";

        public IProviderAuth Auth { get; } = new OAuthAuth
        {
            SupportedAuthMethods = [AuthMethod.OAuthPkce],
            TokenEndpoint = new Uri("https://auth.example.com/token"),
            ClientId = "test-client",
            AuthorizationEndpoint = new Uri("https://auth.example.com/authorize"),
            RedirectUri = new Uri("http://127.0.0.1:1455/auth/callback"),
            Scope = "openid profile email offline_access"
        };

        public Task<ProviderProbeResult> ProbeAsync(ProviderEntry entry, CancellationToken ct = default)
        {
            return Task.FromResult(new ProviderProbeResult(true, null, []));
        }
    }
}
