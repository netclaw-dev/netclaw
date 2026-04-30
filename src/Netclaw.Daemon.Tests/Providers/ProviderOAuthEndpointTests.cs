// -----------------------------------------------------------------------
// <copyright file="ProviderOAuthEndpointTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Netclaw.Configuration;
using Netclaw.Daemon.Providers;
using Netclaw.Providers;
using Netclaw.Providers.OAuth;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class ProviderOAuthEndpointTests
{
    [Fact]
    public async Task StartEndpoint_RequiresAuthorization()
    {
        await using var host = await CreateHostAsync(_ => SuccessfulTokenResponse());
        var client = host.GetTestClient();

        var response = await client.PostAsync("/api/provider/oauth/start?provider=test-oauth", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task StartEndpoint_ReturnsAuthorizationUrl_AndPendingStatus()
    {
        await using var host = await CreateHostAsync(_ => SuccessfulTokenResponse());
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, TestAuthHandler.HeaderValue);

        var startResponse = await client.PostAsync("/api/provider/oauth/start?provider=test-oauth", null, TestContext.Current.CancellationToken);
        startResponse.EnsureSuccessStatusCode();

        var startPayload = await startResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var state = startPayload.GetProperty("state").GetString();
        var authorizationUrl = startPayload.GetProperty("authorizationUrl").GetString();

        Assert.NotNull(state);
        Assert.NotNull(authorizationUrl);
        Assert.Contains("code_challenge=", authorizationUrl);
        Assert.Contains($"state={state}", authorizationUrl);

        var statusPayload = await client.GetFromJsonAsync<JsonElement>($"/api/provider/oauth/status/{state}", TestContext.Current.CancellationToken);
        Assert.Equal("Pending", statusPayload.GetProperty("status").GetString());
        Assert.False(statusPayload.GetProperty("hasToken").GetBoolean());
    }

    [Fact]
    public async Task CallbackEndpoint_CompletesFlow_AndStatusReturnsTokens()
    {
        await using var host = await CreateHostAsync(_ => SuccessfulTokenResponse());
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, TestAuthHandler.HeaderValue);

        var startResponse = await client.PostAsync("/api/provider/oauth/start?provider=test-oauth", null, TestContext.Current.CancellationToken);
        var startPayload = await startResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var state = startPayload.GetProperty("state").GetString();

        var callbackResponse = await client.GetAsync($"/api/provider/oauth/callback?code=test-code&state={state}", TestContext.Current.CancellationToken);
        callbackResponse.EnsureSuccessStatusCode();
        var html = await callbackResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Authorization complete", html);

        var statusPayload = await client.GetFromJsonAsync<JsonElement>($"/api/provider/oauth/status/{state}", TestContext.Current.CancellationToken);
        Assert.Equal("Completed", statusPayload.GetProperty("status").GetString());
        Assert.True(statusPayload.GetProperty("hasToken").GetBoolean());
        Assert.Equal("access-token", statusPayload.GetProperty("accessToken").GetString());
        Assert.Equal("refresh-token", statusPayload.GetProperty("refreshToken").GetString());
    }

    [Fact]
    public async Task StatusEndpoint_HidesTokens_ForNonLoopbackRequests()
    {
        await using var host = await CreateHostAsync(_ => SuccessfulTokenResponse(), remoteIp: IPAddress.Parse("192.168.1.100"));
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, TestAuthHandler.HeaderValue);

        var startResponse = await client.PostAsync("/api/provider/oauth/start?provider=test-oauth", null, TestContext.Current.CancellationToken);
        var startPayload = await startResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var state = startPayload.GetProperty("state").GetString();

        var callbackResponse = await client.GetAsync($"/api/provider/oauth/callback?code=test-code&state={state}", TestContext.Current.CancellationToken);
        callbackResponse.EnsureSuccessStatusCode();

        var statusPayload = await client.GetFromJsonAsync<JsonElement>($"/api/provider/oauth/status/{state}", TestContext.Current.CancellationToken);
        Assert.Equal("Completed", statusPayload.GetProperty("status").GetString());
        Assert.True(statusPayload.GetProperty("hasToken").GetBoolean());
        Assert.Equal(JsonValueKind.Null, statusPayload.GetProperty("accessToken").ValueKind);
        Assert.Equal(JsonValueKind.Null, statusPayload.GetProperty("refreshToken").ValueKind);
    }

    [Fact]
    public async Task CallbackEndpoint_OnExchangeFailure_Returns500_AndFailedStatus()
    {
        await using var host = await CreateHostAsync(_ =>
            JsonResponse(new { error = "invalid_request" }, HttpStatusCode.BadRequest));
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, TestAuthHandler.HeaderValue);

        var startResponse = await client.PostAsync("/api/provider/oauth/start?provider=test-oauth", null, TestContext.Current.CancellationToken);
        var startPayload = await startResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var state = startPayload.GetProperty("state").GetString();

        var callbackResponse = await client.GetAsync($"/api/provider/oauth/callback?code=bad-code&state={state}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, callbackResponse.StatusCode);
        var html = await callbackResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Authorization failed", html);

        var statusPayload = await client.GetFromJsonAsync<JsonElement>($"/api/provider/oauth/status/{state}", TestContext.Current.CancellationToken);
        Assert.Equal("Failed", statusPayload.GetProperty("status").GetString());
        Assert.False(statusPayload.GetProperty("hasToken").GetBoolean());
    }

    private static async Task<WebApplication> CreateHostAsync(
        Func<HttpRequestMessage, HttpResponseMessage> tokenHandler,
        IPAddress? remoteIp = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services
            .AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(new OAuthPkceService(new HttpClient(new FakeHttpMessageHandler(tokenHandler))));
        builder.Services.AddSingleton<IProviderOAuthCallbackListener, NoOpProviderOAuthCallbackListener>();
        builder.Services.AddSingleton(new ProviderDescriptorRegistry([new TestOAuthDescriptor()]));

        var app = builder.Build();
        var effectiveIp = remoteIp ?? IPAddress.Loopback;
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress ??= effectiveIp;
            await next();
        });
        app.UseAuthentication();
        app.UseAuthorization();
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

    private sealed class NoOpProviderOAuthCallbackListener : IProviderOAuthCallbackListener
    {
        public void StartListening(string redirectUri, string state)
        {
        }
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "TestAuth";
        public const string HeaderName = "X-Test-Auth";
        public const string HeaderValue = "ok";

        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(HeaderName, out var value) || value != HeaderValue)
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "test-user")], SchemeName);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
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
