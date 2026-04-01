using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Providers.OAuth;
using Xunit;
using static Netclaw.Configuration.Tests.Providers.OAuth.OAuthTestHelpers;

namespace Netclaw.Configuration.Tests.Providers.OAuth;

public class OpenAiDeviceFlowServiceTests
{
    private static readonly OAuthDeviceFlowConfig TestConfig = new(
        DeviceAuthorizationEndpoint: "https://auth.openai.com/api/accounts/deviceauth/usercode",
        TokenEndpoint: "https://auth.openai.com/api/accounts/deviceauth/token",
        ClientId: "test-client-id",
        Scope: "openid profile email offline_access model.request api.model.read",
        PkceExchangeEndpoint: "https://auth.openai.com/oauth/token");

    [Fact]
    public async Task StartDeviceAuthorization_PostsJsonWithClientId_ReturnsUserCode()
    {
        string? capturedBody = null;
        string? capturedContentType = null;

        var handler = new FakeHttpMessageHandler(request =>
        {
            capturedBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            capturedContentType = request.Content?.Headers.ContentType?.MediaType;
            return JsonResponse(new
            {
                device_auth_id = "daid-123",
                user_code = "ABCD-1234",
                interval = 5,
                expires_in = 1200
            });
        });

        var service = new OpenAiDeviceFlowService(new HttpClient(handler));
        var result = await service.StartDeviceAuthorizationAsync(TestConfig, TestContext.Current.CancellationToken);

        // Verify JSON body with client_id and scope
        Assert.Equal("application/json", capturedContentType);
        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("test-client-id", doc.RootElement.GetProperty("client_id").GetString());
        Assert.Equal("openid profile email offline_access model.request api.model.read",
            doc.RootElement.GetProperty("scope").GetString());

        // Verify response mapping
        Assert.Equal("daid-123", result.DeviceCode); // device_auth_id -> DeviceCode
        Assert.Equal("ABCD-1234", result.UserCode);
        Assert.Equal("https://auth.openai.com/codex/device", result.VerificationUri);
        Assert.Equal(1200, result.ExpiresIn);
        Assert.Equal(5, result.Interval);
    }

    [Fact]
    public async Task StartDeviceAuthorization_NullScope_OmitsScopeFromJson()
    {
        var configNoScope = new OAuthDeviceFlowConfig(
            DeviceAuthorizationEndpoint: "https://auth.openai.com/api/accounts/deviceauth/usercode",
            TokenEndpoint: "https://auth.openai.com/api/accounts/deviceauth/token",
            ClientId: "test-client-id",
            PkceExchangeEndpoint: "https://auth.openai.com/oauth/token");

        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            capturedBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(new
            {
                device_auth_id = "daid-123",
                user_code = "ABCD-1234",
                interval = 5
            });
        });

        var service = new OpenAiDeviceFlowService(new HttpClient(handler));
        await service.StartDeviceAuthorizationAsync(configNoScope, TestContext.Current.CancellationToken);

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.False(doc.RootElement.TryGetProperty("scope", out _));
    }

    [Fact]
    public async Task StartDeviceAuthorization_MissingExpiresIn_UsesDefaultExpiry()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            JsonResponse(new
            {
                device_auth_id = "daid-123",
                user_code = "ABCD-1234",
                interval = 5
            }));

        var service = new OpenAiDeviceFlowService(new HttpClient(handler));
        var result = await service.StartDeviceAuthorizationAsync(TestConfig, TestContext.Current.CancellationToken);

        Assert.Equal(900, result.ExpiresIn);
    }

    [Fact]
    public async Task StartDeviceAuthorization_404_ThrowsWithGuidance()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        var service = new OpenAiDeviceFlowService(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartDeviceAuthorizationAsync(TestConfig, TestContext.Current.CancellationToken));

        Assert.Contains("not available", ex.Message);
        Assert.Contains("API key", ex.Message);
    }

    [Fact]
    public async Task PollForToken_403Pending_ThenSuccess_ExchangesForToken()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(request =>
        {
            callCount++;
            var url = request.RequestUri!.ToString();

            // First two calls are polls returning 403 (pending)
            if (url.Contains("deviceauth/token") && callCount <= 2)
                return new HttpResponseMessage(HttpStatusCode.Forbidden);

            // Third poll returns auth code
            if (url.Contains("deviceauth/token"))
            {
                return JsonResponse(new
                {
                    authorization_code = "auth-code-123",
                    code_verifier = "verifier-xyz"
                });
            }

            // Token exchange
            if (url.Contains("oauth/token"))
            {
                return JsonResponse(new
                {
                    access_token = "at-secret",
                    refresh_token = "rt-secret",
                    expires_in = 3600
                });
            }

            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var timeProvider = new FakeTimeProvider();
        var service = new OpenAiDeviceFlowService(new HttpClient(handler), timeProvider);
        var deviceAuth = new DeviceAuthorizationResponse(
            DeviceCode: "daid-test",
            UserCode: "UC-TEST",
            VerificationUri: "https://auth.openai.com/codex/device",
            ExpiresIn: 30,
            Interval: 1);

        var states = new List<DeviceFlowState>();
        var pollTask = service.PollForTokenAsync(TestConfig, deviceAuth, s => states.Add(s), TestContext.Current.CancellationToken);

        // Advance time to trigger each poll interval
        for (var i = 0; i < 3; i++)
            timeProvider.Advance(TimeSpan.FromSeconds(1));

        var result = await pollTask;

        Assert.Equal("at-secret", result.AccessToken.Value);
        Assert.NotNull(result.RefreshToken);
        Assert.Equal("rt-secret", result.RefreshToken!.Value);
        Assert.NotNull(result.ExpiresAt);
        Assert.Contains(DeviceFlowState.WaitingForUser, states);
        Assert.Contains(DeviceFlowState.Succeeded, states);
        // 2 pending polls + 1 success poll + 1 token exchange = 4 calls total
        Assert.Equal(4, callCount);
    }

    [Fact]
    public async Task PollForToken_5xxTransient_KeepsPolling()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(request =>
        {
            callCount++;
            var url = request.RequestUri!.ToString();

            if (url.Contains("deviceauth/token"))
            {
                // First call returns 502 (transient)
                if (callCount == 1)
                    return new HttpResponseMessage(HttpStatusCode.BadGateway);

                // Second call returns auth code
                return JsonResponse(new
                {
                    authorization_code = "auth-code-123",
                    code_verifier = "verifier-xyz"
                });
            }

            // Token exchange
            if (url.Contains("oauth/token"))
            {
                return JsonResponse(new
                {
                    access_token = "at-secret",
                    expires_in = 3600
                });
            }

            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var timeProvider = new FakeTimeProvider();
        var service = new OpenAiDeviceFlowService(new HttpClient(handler), timeProvider);
        var deviceAuth = new DeviceAuthorizationResponse(
            "daid", "UC", "https://auth.openai.com/codex/device", 30, 1);

        var pollTask = service.PollForTokenAsync(TestConfig, deviceAuth, ct: TestContext.Current.CancellationToken);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        timeProvider.Advance(TimeSpan.FromSeconds(1));

        var result = await pollTask;

        Assert.Equal("at-secret", result.AccessToken.Value);
        // 1 transient 502 + 1 success poll + 1 token exchange = 3 calls
        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task PollForToken_404_FailsFastWithConfigurationGuidance()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        var timeProvider = new FakeTimeProvider();
        var service = new OpenAiDeviceFlowService(new HttpClient(handler), timeProvider);
        var deviceAuth = new DeviceAuthorizationResponse(
            "daid", "UC", "https://auth.openai.com/codex/device", 30, 1);

        var pollTask = service.PollForTokenAsync(TestConfig, deviceAuth, ct: TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromSeconds(1));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => pollTask);
        Assert.Contains("endpoint", ex.Message);
        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public async Task PollForToken_PkceExchangeFailure_Propagates()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();

            // Poll returns auth code immediately
            if (url.Contains("deviceauth/token"))
            {
                return JsonResponse(new
                {
                    authorization_code = "auth-code-123",
                    code_verifier = "verifier-xyz"
                });
            }

            // PKCE exchange fails with 400
            if (url.Contains("oauth/token"))
            {
                return JsonResponse(
                    new { error = "invalid_grant", error_description = "code_verifier mismatch" },
                    HttpStatusCode.BadRequest);
            }

            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var timeProvider = new FakeTimeProvider();
        var service = new OpenAiDeviceFlowService(new HttpClient(handler), timeProvider);
        var deviceAuth = new DeviceAuthorizationResponse(
            "daid", "UC", "https://auth.openai.com/codex/device", 30, 1);

        var pollTask = service.PollForTokenAsync(TestConfig, deviceAuth, ct: TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromSeconds(1));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => pollTask);
        Assert.Contains("400", ex.Message);
    }

    [Fact]
    public async Task PollForToken_Cancellation_ThrowsOperationCanceled()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden));

        var timeProvider = new FakeTimeProvider();
        var service = new OpenAiDeviceFlowService(new HttpClient(handler), timeProvider);
        var deviceAuth = new DeviceAuthorizationResponse(
            "daid", "UC", "https://auth.openai.com/codex/device", 60, 1);

        using var cts = new CancellationTokenSource();
        var pollTask = service.PollForTokenAsync(TestConfig, deviceAuth, ct: cts.Token);

        cts.Cancel();
        timeProvider.Advance(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pollTask);
    }

    [Fact]
    public async Task RefreshToken_Success_ReturnsNewToken()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            JsonResponse(new
            {
                access_token = "new-at",
                refresh_token = "new-rt",
                expires_in = 3600
            }));

        var service = new OpenAiDeviceFlowService(new HttpClient(handler));

        var result = await service.RefreshTokenAsync(
            "https://auth.openai.com/oauth/token",
            "client-id",
            new SensitiveString("old-refresh-token"), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("new-at", result!.AccessToken.Value);
        Assert.NotNull(result.RefreshToken);
        Assert.Equal("new-rt", result.RefreshToken!.Value);
    }

    [Fact]
    public async Task RefreshToken_InvalidGrant_ReturnsNull()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            JsonResponse(new { error = "invalid_grant" }, HttpStatusCode.BadRequest));

        var service = new OpenAiDeviceFlowService(new HttpClient(handler));

        var result = await service.RefreshTokenAsync(
            "https://auth.openai.com/oauth/token",
            "client-id",
            new SensitiveString("expired-refresh-token"), TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task RefreshToken_StringOverload_RemainsSupported()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            JsonResponse(new
            {
                access_token = "new-at",
                expires_in = 3600
            }));

        var service = new OpenAiDeviceFlowService(new HttpClient(handler));

        var result = await service.RefreshTokenAsync(
            "https://auth.openai.com/oauth/token",
            "client-id",
            "old-refresh-token", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("new-at", result!.AccessToken.Value);
    }
}
