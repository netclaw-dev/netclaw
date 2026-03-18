using System.Net;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Providers.OAuth;
using Xunit;
using static Netclaw.Configuration.Tests.Providers.OAuth.OAuthTestHelpers;

namespace Netclaw.Configuration.Tests.Providers.OAuth;

public class OAuthDeviceFlowServiceTests
{
    private static readonly OAuthDeviceFlowConfig TestConfig = new(
        "https://auth.example.com/device",
        "https://auth.example.com/token",
        "test-client-id");

    [Fact]
    public async Task StartDeviceAuthorization_ReturnsDeviceAuthResponse()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            JsonResponse(new
            {
                device_code = "dc-123",
                user_code = "USER-CODE",
                verification_uri = "https://auth.example.com/verify",
                expires_in = 300,
                interval = 5
            }));

        var service = new OAuthDeviceFlowService(new HttpClient(handler));

        var result = await service.StartDeviceAuthorizationAsync(TestConfig);

        Assert.Equal("dc-123", result.DeviceCode);
        Assert.Equal("USER-CODE", result.UserCode);
        Assert.Equal("https://auth.example.com/verify", result.VerificationUri);
        Assert.Equal(300, result.ExpiresIn);
        Assert.Equal(5, result.Interval);
    }

    [Fact]
    public async Task PollForToken_PendingThenSuccess_ReturnsToken()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(request =>
        {
            callCount++;
            if (callCount <= 2)
                return JsonResponse(new { error = "authorization_pending" }, HttpStatusCode.BadRequest);

            return JsonResponse(new
            {
                access_token = "at-secret",
                refresh_token = "rt-secret",
                expires_in = 3600
            });
        });

        var timeProvider = new FakeTimeProvider();
        var service = new OAuthDeviceFlowService(new HttpClient(handler), timeProvider);

        var deviceAuth = new DeviceAuthorizationResponse(
            DeviceCode: "dc-test",
            UserCode: "UC-TEST",
            VerificationUri: "https://auth.example.com/verify",
            ExpiresIn: 30,
            Interval: 1);

        var states = new List<DeviceFlowState>();
        var pollTask = service.PollForTokenAsync(TestConfig, deviceAuth, s => states.Add(s));

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
        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task PollForToken_SlowDown_IncreasesInterval()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(request =>
        {
            callCount++;
            if (callCount == 1)
                return JsonResponse(new { error = "slow_down" }, HttpStatusCode.BadRequest);

            return JsonResponse(new { access_token = "token", expires_in = 3600 });
        });

        var timeProvider = new FakeTimeProvider();
        var service = new OAuthDeviceFlowService(new HttpClient(handler), timeProvider);
        var deviceAuth = new DeviceAuthorizationResponse("dc", "UC", "https://x.com/v", 60, 1);

        var pollTask = service.PollForTokenAsync(TestConfig, deviceAuth);

        // First poll at 1s interval
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        // After slow_down, interval increases to 6s (1+5)
        timeProvider.Advance(TimeSpan.FromSeconds(6));

        var result = await pollTask;

        Assert.Equal("token", result.AccessToken.Value);
        Assert.True(callCount >= 2);
    }

    [Fact]
    public async Task PollForToken_AccessDenied_ThrowsDeniedException()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            JsonResponse(new { error = "access_denied" }, HttpStatusCode.BadRequest));

        var timeProvider = new FakeTimeProvider();
        var service = new OAuthDeviceFlowService(new HttpClient(handler), timeProvider);
        var deviceAuth = new DeviceAuthorizationResponse("dc", "UC", "https://x.com/v", 60, 1);

        var pollTask = service.PollForTokenAsync(TestConfig, deviceAuth);
        timeProvider.Advance(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<OAuthDeviceFlowDeniedException>(() => pollTask);
    }

    [Fact]
    public async Task PollForToken_ExpiredToken_ThrowsExpiredException()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            JsonResponse(new { error = "expired_token" }, HttpStatusCode.BadRequest));

        var timeProvider = new FakeTimeProvider();
        var service = new OAuthDeviceFlowService(new HttpClient(handler), timeProvider);
        var deviceAuth = new DeviceAuthorizationResponse("dc", "UC", "https://x.com/v", 60, 1);

        var pollTask = service.PollForTokenAsync(TestConfig, deviceAuth);
        timeProvider.Advance(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<OAuthDeviceFlowExpiredException>(() => pollTask);
    }

    [Fact]
    public async Task PollForToken_Cancellation_ThrowsOperationCanceled()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            JsonResponse(new { error = "authorization_pending" }, HttpStatusCode.BadRequest));

        var timeProvider = new FakeTimeProvider();
        var service = new OAuthDeviceFlowService(new HttpClient(handler), timeProvider);
        var deviceAuth = new DeviceAuthorizationResponse("dc", "UC", "https://x.com/v", 60, 1);

        using var cts = new CancellationTokenSource();
        var pollTask = service.PollForTokenAsync(TestConfig, deviceAuth, ct: cts.Token);

        // Cancel before advancing time
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

        var service = new OAuthDeviceFlowService(new HttpClient(handler));

        var result = await service.RefreshTokenAsync(
            "https://auth.example.com/token",
            "client-id",
            new SensitiveString("old-refresh-token"));

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

        var service = new OAuthDeviceFlowService(new HttpClient(handler));

        var result = await service.RefreshTokenAsync(
            "https://auth.example.com/token",
            "client-id",
            new SensitiveString("expired-refresh-token"));

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

        var service = new OAuthDeviceFlowService(new HttpClient(handler));

        var result = await service.RefreshTokenAsync(
            "https://auth.example.com/token",
            "client-id",
            "old-refresh-token");

        Assert.NotNull(result);
        Assert.Equal("new-at", result!.AccessToken.Value);
    }
}
