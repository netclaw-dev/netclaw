using System.Net;
using System.Text;
using System.Text.Json;
using Netclaw.Configuration.Providers.OAuth;
using Xunit;

namespace Netclaw.Configuration.Tests.Providers.OAuth;

public class OpenAiDeviceFlowServiceTests
{
    private static readonly OAuthDeviceFlowConfig TestConfig = new(
        DeviceAuthorizationEndpoint: "https://auth.openai.com/api/accounts/deviceauth/usercode",
        TokenEndpoint: "https://auth.openai.com/api/accounts/deviceauth/token",
        ClientId: "test-client-id",
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
                interval = 5
            });
        });

        var service = new OpenAiDeviceFlowService(new HttpClient(handler));
        var result = await service.StartDeviceAuthorizationAsync(TestConfig);

        // Verify JSON body with client_id
        Assert.Equal("application/json", capturedContentType);
        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("test-client-id", doc.RootElement.GetProperty("client_id").GetString());

        // Verify response mapping
        Assert.Equal("daid-123", result.DeviceCode); // device_auth_id → DeviceCode
        Assert.Equal("ABCD-1234", result.UserCode);
        Assert.Equal("https://auth.openai.com/codex/device", result.VerificationUri);
        Assert.Equal(5, result.Interval);
    }

    [Fact]
    public async Task StartDeviceAuthorization_404_ThrowsWithGuidance()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        var service = new OpenAiDeviceFlowService(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartDeviceAuthorizationAsync(TestConfig));

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
                    code_challenge = "challenge-abc",
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

        var service = new OpenAiDeviceFlowService(new HttpClient(handler));
        var deviceAuth = new DeviceAuthorizationResponse(
            DeviceCode: "daid-test",
            UserCode: "UC-TEST",
            VerificationUri: "https://auth.openai.com/codex/device",
            ExpiresIn: 30,
            Interval: 1);

        var states = new List<DeviceFlowState>();
        var result = await service.PollForTokenAsync(TestConfig, deviceAuth, s => states.Add(s));

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
    public async Task PollForToken_Cancellation_ThrowsOperationCanceled()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden));

        var service = new OpenAiDeviceFlowService(new HttpClient(handler));
        var deviceAuth = new DeviceAuthorizationResponse(
            "daid", "UC", "https://auth.openai.com/codex/device", 60, 1);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.PollForTokenAsync(TestConfig, deviceAuth, ct: cts.Token));
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
            "old-refresh-token");

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
            "expired-refresh-token");

        Assert.Null(result);
    }

    // ── Helpers ──

    private static HttpResponseMessage JsonResponse(object body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var json = JsonSerializer.Serialize(body);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_handler(request));
        }
    }
}
