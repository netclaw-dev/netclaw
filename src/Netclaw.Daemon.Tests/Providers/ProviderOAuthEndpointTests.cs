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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Channels;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Daemon.Providers;
using Netclaw.Daemon.Security;
using Netclaw.Daemon.Tests.Security;
using Netclaw.Providers;
using Netclaw.Providers.OAuth;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class ProviderOAuthEndpointTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();

    public void Dispose() => _dir.Dispose();

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

    public static TheoryData<ExposureMode, bool> StatusCallers => new()
    {
        { ExposureMode.Local, false },
        { ExposureMode.Local, true },
        { ExposureMode.TailscaleServe, true },
        { ExposureMode.TailscaleFunnel, true },
        { ExposureMode.CloudflareTunnel, true },
        { ExposureMode.ReverseProxy, true },
    };

    [Theory]
    [MemberData(nameof(StatusCallers))]
    public async Task StatusEndpoint_NeverReturnsTokens(ExposureMode mode, bool withDeviceToken)
    {
        // A tunnel agent connects from loopback, so every caller here has a loopback
        // RemoteIpAddress. Local mode without a bearer token is the LocalProcess caller.
        await using var host = await CreateExposureHostAsync(mode);
        var client = host.GetTestClient();
        var state = await CompleteFlowAsync(client, host);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/provider/oauth/status/{state}");
        if (withDeviceToken)
            request.Headers.Authorization = new("Bearer", host.DeviceToken);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var statusPayload = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("Completed", statusPayload.GetProperty("status").GetString());
        Assert.True(statusPayload.GetProperty("hasToken").GetBoolean());
        AssertNoTokens(statusPayload);
    }

    [Theory]
    [InlineData(ExposureMode.TailscaleServe)]
    [InlineData(ExposureMode.CloudflareTunnel)]
    public async Task StatusEndpoint_RejectsUnauthenticatedLoopbackCaller_InTunnelModes(ExposureMode mode)
    {
        await using var host = await CreateExposureHostAsync(mode);
        var client = host.GetTestClient();
        var state = await CompleteFlowAsync(client, host);

        var response = await client.GetAsync($"/api/provider/oauth/status/{state}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(ExposureMode.Local)]
    [InlineData(ExposureMode.TailscaleServe)]
    [InlineData(ExposureMode.TailscaleFunnel)]
    [InlineData(ExposureMode.CloudflareTunnel)]
    [InlineData(ExposureMode.ReverseProxy)]
    public async Task ResultEndpoint_ReturnsTokens_ForStateBoundHostProof(ExposureMode mode)
    {
        await using var host = await CreateExposureHostAsync(mode);
        var client = host.GetTestClient();
        var state = await CompleteFlowAsync(client, host);
        var proof = host.CreateProof(LocalControlOperation.ReadProviderOAuthResult(state));

        var response = await PostResultAsync(client, state, proof);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("access-token", payload.GetProperty("accessToken").GetString());
        Assert.Equal("refresh-token", payload.GetProperty("refreshToken").GetString());
        Assert.Equal("account-id", payload.GetProperty("accountId").GetString());
    }

    [Theory]
    [InlineData(ExposureMode.Local)]
    [InlineData(ExposureMode.TailscaleServe)]
    [InlineData(ExposureMode.TailscaleFunnel)]
    [InlineData(ExposureMode.CloudflareTunnel)]
    public async Task ResultEndpoint_RefusesDeviceToken_WithoutProof(ExposureMode mode)
    {
        await using var host = await CreateExposureHostAsync(mode);
        var client = host.GetTestClient();
        var state = await CompleteFlowAsync(client, host);

        var response = await PostResultAsync(client, state, proof: "", bearer: host.DeviceToken);

        await AssertRefusedAsync(response);
    }

    [Fact]
    public async Task ResultEndpoint_RefusesPairingProof()
    {
        await using var host = await CreateExposureHostAsync(ExposureMode.TailscaleServe);
        var client = host.GetTestClient();
        var state = await CompleteFlowAsync(client, host);
        var proof = host.CreateProof(LocalControlOperation.GeneratePairingCode);

        var response = await PostResultAsync(client, state, proof);

        await AssertRefusedAsync(response);
    }

    [Fact]
    public async Task ResultEndpoint_RefusesProofForAnotherState()
    {
        await using var host = await CreateExposureHostAsync(ExposureMode.CloudflareTunnel);
        var client = host.GetTestClient();
        var state = await CompleteFlowAsync(client, host);
        var otherState = await CompleteFlowAsync(client, host);
        var proof = host.CreateProof(LocalControlOperation.ReadProviderOAuthResult(otherState));

        var response = await PostResultAsync(client, state, proof);

        await AssertRefusedAsync(response);
    }

    [Fact]
    public async Task ResultEndpoint_RefusesReplayedProof()
    {
        await using var host = await CreateExposureHostAsync(ExposureMode.TailscaleFunnel);
        var client = host.GetTestClient();
        var state = await CompleteFlowAsync(client, host);
        var proof = host.CreateProof(LocalControlOperation.ReadProviderOAuthResult(state));

        var first = await PostResultAsync(client, state, proof);
        var replay = await PostResultAsync(client, state, proof);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await AssertRefusedAsync(replay);
    }

    [Fact]
    public async Task ResultEndpoint_RefusesExpiredProof()
    {
        await using var host = await CreateExposureHostAsync(ExposureMode.TailscaleServe);
        var client = host.GetTestClient();
        var state = await CompleteFlowAsync(client, host);
        var proof = host.CreateProof(
            LocalControlOperation.ReadProviderOAuthResult(state),
            issuedAt: host.Time.GetUtcNow() - LocalControlPairingProofValidator.ProofLifetime - TimeSpan.FromSeconds(1));

        var response = await PostResultAsync(client, state, proof);

        await AssertRefusedAsync(response);
    }

    [Fact]
    public async Task ResultEndpoint_ReturnsConflict_WhenFlowHasNoResult()
    {
        await using var host = await CreateExposureHostAsync(ExposureMode.Local);
        var client = host.GetTestClient();
        var startResponse = await client.PostAsync("/api/provider/oauth/start?provider=test-oauth", null, TestContext.Current.CancellationToken);
        var startPayload = await startResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var state = startPayload.GetProperty("state").GetString()!;
        var proof = host.CreateProof(LocalControlOperation.ReadProviderOAuthResult(state));

        var response = await PostResultAsync(client, state, proof);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ResultEndpoint_ReturnsTokens_DuringAFloodOfRefusedRequests()
    {
        // A tunnel agent forwards every remote request from loopback, so remote junk and
        // the host CLI share one source address. Only refused requests count.
        await using var host = await CreateExposureHostAsync(ExposureMode.TailscaleServe);
        var client = host.GetTestClient();
        var state = await CompleteFlowAsync(client, host);

        var flood = new List<HttpStatusCode>();
        for (var index = 0; index < 30; index++)
            flood.Add((await PostResultAsync(client, state, proof: "not-a-proof")).StatusCode);
        var response = await PostResultAsync(
            client,
            state,
            host.CreateProof(LocalControlOperation.ReadProviderOAuthResult(state)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            Enumerable.Repeat(HttpStatusCode.Unauthorized, 10).Concat(Enumerable.Repeat(HttpStatusCode.TooManyRequests, 20)),
            flood);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("access-token", payload.GetProperty("accessToken").GetString());
    }

    [Fact]
    public async Task ResultEndpoint_RefusalLimit_ResetsAfterTheWindow()
    {
        await using var host = await CreateExposureHostAsync(ExposureMode.CloudflareTunnel);
        var client = host.GetTestClient();
        var state = await CompleteFlowAsync(client, host);
        for (var index = 0; index < 11; index++)
            await PostResultAsync(client, state, proof: "not-a-proof");

        host.Time.Advance(TimeSpan.FromSeconds(1));
        var afterWindow = await PostResultAsync(client, state, proof: "not-a-proof");

        await AssertRefusedAsync(afterWindow);
    }

    private static Task<HttpResponseMessage> PostResultAsync(
        HttpClient client,
        string state,
        string proof,
        string? bearer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/local-control/v1/provider-oauth-result")
        {
            Content = JsonContent.Create(new { state, proof }),
        };
        if (bearer is not null)
            request.Headers.Authorization = new("Bearer", bearer);

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("access-token", body);
        Assert.DoesNotContain("refresh-token", body);
    }

    private static void AssertNoTokens(JsonElement payload)
    {
        Assert.False(payload.TryGetProperty("accessToken", out _));
        Assert.False(payload.TryGetProperty("refreshToken", out _));
        Assert.False(payload.TryGetProperty("accountId", out _));
    }

    private static async Task<string> CompleteFlowAsync(HttpClient client, ExposureHost host)
    {
        var startRequest = new HttpRequestMessage(HttpMethod.Post, "/api/provider/oauth/start?provider=test-oauth");
        if (host.Mode != ExposureMode.Local)
            startRequest.Headers.Authorization = new("Bearer", host.DeviceToken);
        var startResponse = await client.SendAsync(startRequest, TestContext.Current.CancellationToken);
        startResponse.EnsureSuccessStatusCode();
        var startPayload = await startResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var state = startPayload.GetProperty("state").GetString()!;

        var callbackResponse = await client.GetAsync($"/api/provider/oauth/callback?code=test-code&state={state}", TestContext.Current.CancellationToken);
        callbackResponse.EnsureSuccessStatusCode();
        return state;
    }

    private async Task<ExposureHost> CreateExposureHostAsync(ExposureMode mode)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero));
        var registry = new DeviceRegistry(new NetclawPaths(_dir.Path), time, NullLogger<DeviceRegistry>.Instance);
        var (rawToken, device) = DeviceTestHelpers.MakeDevice("remote-phone", time.GetUtcNow());
        await registry.AddAsync(device, TestContext.Current.CancellationToken);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(registry);
        builder.Services.AddNetclawAuthSchemes(new DaemonConfig { ExposureMode = mode });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<ClaimsPrincipalMapper>();
        builder.Services.AddSingleton(new OAuthPkceService(new HttpClient(new FakeHttpMessageHandler(_ => SuccessfulTokenResponse()))));
        builder.Services.AddSingleton<IProviderOAuthCallbackListener, NoOpProviderOAuthCallbackListener>();
        builder.Services.AddSingleton(new ProviderDescriptorRegistry([new TestOAuthDescriptor()]));
        var protector = new LocalControlPairingProofProtector(
            SecretsProtection.CreateDataProtectionProvider(new NetclawPaths(_dir.Path)));
        builder.Services.AddSingleton(protector);
        builder.Services.AddSingleton(new LocalControlPairingProofValidator(
            protector,
            time,
            NullLogger<LocalControlPairingProofValidator>.Instance));
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddSingleton<LocalControlRequests>();

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            await next();
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapProviderOAuthEndpoints();
        await app.StartAsync();
        return new ExposureHost(app, mode, rawToken, protector, time);
    }

    private sealed class ExposureHost(
        WebApplication app,
        ExposureMode mode,
        string deviceToken,
        LocalControlPairingProofProtector protector,
        FakeTimeProvider time) : IAsyncDisposable
    {
        public ExposureMode Mode { get; } = mode;
        public string DeviceToken { get; } = deviceToken;
        public FakeTimeProvider Time { get; } = time;
        public HttpClient GetTestClient() => app.GetTestClient();

        public string CreateProof(LocalControlOperation operation, DateTimeOffset? issuedAt = null)
            => protector.CreateProof(operation, issuedAt ?? Time.GetUtcNow());

        public ValueTask DisposeAsync() => app.DisposeAsync();
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
        Func<HttpRequestMessage, HttpResponseMessage> tokenHandler)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services
            .AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<ClaimsPrincipalMapper>();
        builder.Services.AddSingleton(new OAuthPkceService(new HttpClient(new FakeHttpMessageHandler(tokenHandler))));
        builder.Services.AddSingleton<IProviderOAuthCallbackListener, NoOpProviderOAuthCallbackListener>();
        builder.Services.AddSingleton(new ProviderDescriptorRegistry([new TestOAuthDescriptor()]));

        var app = builder.Build();
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
            id_token = JwtTestToken.Make(new Dictionary<string, object>
            {
                ["https://api.openai.com/auth"] = new Dictionary<string, object>
                {
                    ["chatgpt_account_id"] = "account-id"
                }
            }),
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

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, "test-user"),
                    new Claim(NetclawClaimTypes.PrincipalClassification, nameof(PrincipalClassification.Operator)),
                    new Claim(NetclawClaimTypes.TransportAuthenticity, nameof(TransportAuthenticity.LocalProcess)),
                ],
                SchemeName);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }

    private sealed class TestOAuthDescriptor : IProviderDescriptor
    {
        public string TypeKey => "test-oauth";

        public ThinkingDialect ResolveThinkingDialect(ProviderEntry entry) => ThinkingDialect.Native;
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
