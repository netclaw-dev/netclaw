// -----------------------------------------------------------------------
// <copyright file="OAuthPkceServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Providers.OAuth;
using Netclaw.Tests.Utilities;
using Xunit;
using static Netclaw.Configuration.Tests.Providers.OAuth.OAuthTestHelpers;

namespace Netclaw.Configuration.Tests.Providers.OAuth;

public class OAuthPkceServiceTests
{
    [Fact]
    public void GenerateCodeVerifier_Returns43CharBase64UrlString()
    {
        var verifier = OAuthPkceService.GenerateCodeVerifier();

        Assert.Equal(43, verifier.Length); // 32 bytes → 43 base64url chars (no padding)
        Assert.DoesNotContain("+", verifier);
        Assert.DoesNotContain("/", verifier);
        Assert.DoesNotContain("=", verifier);
    }

    [Fact]
    public void GenerateCodeVerifier_ProducesDifferentValuesEachCall()
    {
        var a = OAuthPkceService.GenerateCodeVerifier();
        var b = OAuthPkceService.GenerateCodeVerifier();

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeCodeChallenge_IsDeterministicForSameVerifier()
    {
        var verifier = OAuthPkceService.GenerateCodeVerifier();

        var challenge1 = OAuthPkceService.ComputeCodeChallenge(verifier);
        var challenge2 = OAuthPkceService.ComputeCodeChallenge(verifier);

        Assert.Equal(challenge1, challenge2);
    }

    [Fact]
    public void ComputeCodeChallenge_DiffersForDifferentVerifiers()
    {
        var a = OAuthPkceService.ComputeCodeChallenge("verifier-a");
        var b = OAuthPkceService.ComputeCodeChallenge("verifier-b");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void StartAuthorizationFlow_BuildsCorrectUrl()
    {
        var service = new OAuthPkceService(new HttpClient());

        var (url, state) = service.StartAuthorizationFlow(
            authorizationEndpoint: "https://auth.example.com/authorize",
            tokenEndpoint: "https://auth.example.com/token",
            clientId: "test-client",
            redirectUri: "http://127.0.0.1:5199/callback",
            scope: "openid profile");

        Assert.Contains("https://auth.example.com/authorize?", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("client_id=test-client", url);
        Assert.Contains("redirect_uri=", url);
        Assert.Contains("code_challenge=", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains($"state={state}", url);
        Assert.Contains("scope=openid%20profile", url);
    }

    [Fact]
    public void StartAuthorizationFlow_OmitsScopeWhenNull()
    {
        var service = new OAuthPkceService(new HttpClient());

        var (url, _) = service.StartAuthorizationFlow(
            authorizationEndpoint: "https://auth.example.com/authorize",
            tokenEndpoint: "https://auth.example.com/token",
            clientId: "test-client",
            redirectUri: "http://127.0.0.1:5199/callback");

        Assert.DoesNotContain("scope=", url);
    }

    [Fact]
    public void StartAuthorizationFlow_CreatesTrackablePendingFlow()
    {
        var service = new OAuthPkceService(new HttpClient());

        var (_, state) = service.StartAuthorizationFlow(
            "https://auth.example.com/authorize",
            "https://auth.example.com/token",
            "test-client",
            "http://127.0.0.1:5199/callback");

        Assert.Equal(OAuthPkceFlowStatus.Pending, service.GetFlowStatus(state));
    }

    [Fact]
    public void GetFlowStatus_UnknownState_ReturnsNotStarted()
    {
        var service = new OAuthPkceService(new HttpClient());

        Assert.Equal(OAuthPkceFlowStatus.NotStarted, service.GetFlowStatus("unknown"));
    }

    [Fact]
    public async Task ExchangeCodeForTokens_SendsCorrectParams_ReturnsTokens()
    {
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            capturedBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(new
            {
                access_token = "at-secret",
                refresh_token = "rt-secret",
                expires_in = 3600
            });
        });

        var timeProvider = new FakeTimeProvider();
        var service = new OAuthPkceService(new HttpClient(handler), timeProvider);

        var result = await service.ExchangeCodeForTokensAsync(
            "https://auth.example.com/token",
            "test-client",
            "auth-code-123",
            "verifier-xyz",
            "http://127.0.0.1:5199/callback", ct: TestContext.Current.CancellationToken);

        Assert.Equal("at-secret", result.AccessToken.Value);
        Assert.NotNull(result.RefreshToken);
        Assert.Equal("rt-secret", result.RefreshToken!.Value);
        Assert.NotNull(result.ExpiresAt);

        // Verify form params
        Assert.NotNull(capturedBody);
        Assert.Contains("grant_type=authorization_code", capturedBody);
        Assert.Contains("client_id=test-client", capturedBody);
        Assert.Contains("code=auth-code-123", capturedBody);
        Assert.Contains("code_verifier=verifier-xyz", capturedBody);
        Assert.Contains("redirect_uri=", capturedBody);
    }

    [Fact]
    public async Task ExchangeCodeForTokens_ExtractsAccountIdFromIdToken()
    {
        var idToken = JwtTestToken.Make(new Dictionary<string, object>
        {
            ["https://api.openai.com/auth"] = new Dictionary<string, object>
            {
                ["chatgpt_account_id"] = "account-from-id-token"
            }
        });
        var handler = new FakeHttpMessageHandler(_ =>
            JsonResponse(new
            {
                access_token = "at-secret",
                refresh_token = "rt-secret",
                id_token = idToken,
                expires_in = "3600"
            }));

        var service = new OAuthPkceService(new HttpClient(handler));

        var result = await service.ExchangeCodeForTokensAsync(
            "https://auth.example.com/token",
            "test-client",
            "auth-code-123",
            "verifier-xyz",
            "http://127.0.0.1:5199/callback", ct: TestContext.Current.CancellationToken);

        Assert.Equal("account-from-id-token", result.AccountId!.Value);
        Assert.NotNull(result.ExpiresAt);
    }

    [Fact]
    public async Task ExchangeCodeForTokens_ExtractsAccountIdFromTopLevelOpenAiClaim()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            JsonResponse(new Dictionary<string, object>
            {
                ["access_token"] = "at-secret",
                ["https://api.openai.com/auth"] = new Dictionary<string, object>
                {
                    ["chatgpt_account_id"] = "account-from-root-claim"
                }
            }));

        var service = new OAuthPkceService(new HttpClient(handler));

        var result = await service.ExchangeCodeForTokensAsync(
            "https://auth.example.com/token",
            "test-client",
            "auth-code-123",
            "verifier-xyz",
            "http://127.0.0.1:5199/callback", ct: TestContext.Current.CancellationToken);

        Assert.Equal("account-from-root-claim", result.AccountId!.Value);
    }

    [Fact]
    public async Task CompleteAuthorizationAsync_ExchangesCodeAndSignalsCompletion()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            JsonResponse(new
            {
                access_token = "at-from-callback",
                refresh_token = "rt-from-callback",
                expires_in = 7200
            }));

        var service = new OAuthPkceService(new HttpClient(handler));

        var (_, state) = service.StartAuthorizationFlow(
            "https://auth.example.com/authorize",
            "https://auth.example.com/token",
            "test-client",
            "http://127.0.0.1:5199/callback",
            "openid");

        var result = await service.CompleteAuthorizationAsync("callback-code", state, TestContext.Current.CancellationToken);

        Assert.Equal("at-from-callback", result.AccessToken.Value);
        Assert.Equal("rt-from-callback", result.RefreshToken!.Value);
    }

    [Fact]
    public async Task CompleteAuthorizationAsync_UnknownState_Throws()
    {
        var service = new OAuthPkceService(new HttpClient());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CompleteAuthorizationAsync("code", "unknown-state", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompleteAuthorizationAsync_TokenExchangeFailure_MarksFlowFailed()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            JsonResponse(new { error = "invalid_request" }, HttpStatusCode.BadRequest));

        var service = new OAuthPkceService(new HttpClient(handler));

        var (_, state) = service.StartAuthorizationFlow(
            "https://auth.example.com/authorize",
            "https://auth.example.com/token",
            "test-client",
            "http://127.0.0.1:5199/callback",
            "openid");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.CompleteAuthorizationAsync("callback-code", state, TestContext.Current.CancellationToken));

        Assert.Equal(OAuthPkceFlowStatus.Failed, service.GetFlowStatus(state));
    }

    [Fact]
    public async Task RefreshToken_Success_ReturnsNewTokenPreservingRefresh()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            JsonResponse(new
            {
                access_token = "new-at",
                expires_in = 3600
                // no refresh_token in response
            }));

        var service = new OAuthPkceService(new HttpClient(handler));

        var result = await service.RefreshTokenAsync(
            "https://auth.example.com/token",
            "test-client",
            new SensitiveString("old-refresh"), ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("new-at", result!.AccessToken.Value);
        // Should preserve the old refresh token since server didn't issue a new one
        Assert.Equal("old-refresh", result.RefreshToken!.Value);
    }

    [Fact]
    public async Task RefreshToken_InvalidGrant_ReturnsNull()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            JsonResponse(new { error = "invalid_grant" }, HttpStatusCode.BadRequest));

        var service = new OAuthPkceService(new HttpClient(handler));

        var result = await service.RefreshTokenAsync(
            "https://auth.example.com/token",
            "test-client",
            new SensitiveString("expired-refresh"), ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public void StartAuthorizationFlow_WithExtraParams_IncludesThemInUrl()
    {
        var service = new OAuthPkceService(new HttpClient());

        var extraParams = new Dictionary<string, string>
        {
            ["resource"] = "https://mcp.example.com/mcp",
        };

        var (url, _) = service.StartAuthorizationFlow(
            "https://auth.example.com/authorize",
            "https://auth.example.com/token",
            "test-client",
            "http://127.0.0.1:5199/callback",
            extraParams: extraParams);

        Assert.Contains("resource=https%3A%2F%2Fmcp.example.com%2Fmcp", url);
    }

    [Fact]
    public async Task ExchangeCodeForTokens_WithExtraParams_MergesIntoRequest()
    {
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            capturedBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(new
            {
                access_token = "at-secret",
                expires_in = 3600
            });
        });

        var service = new OAuthPkceService(new HttpClient(handler));

        var extraParams = new Dictionary<string, string>
        {
            ["resource"] = "https://mcp.example.com/mcp",
        };

        await service.ExchangeCodeForTokensAsync(
            "https://auth.example.com/token",
            "test-client",
            "auth-code",
            "verifier",
            "http://127.0.0.1:5199/callback",
            extraParams, TestContext.Current.CancellationToken);

        Assert.NotNull(capturedBody);
        Assert.Contains("resource=https%3A%2F%2Fmcp.example.com%2Fmcp", capturedBody);
        Assert.Contains("grant_type=authorization_code", capturedBody);
    }

    [Fact]
    public async Task RefreshToken_WithExtraParams_MergesIntoRequest()
    {
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            capturedBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(new
            {
                access_token = "new-at",
                expires_in = 3600
            });
        });

        var service = new OAuthPkceService(new HttpClient(handler));

        var extraParams = new Dictionary<string, string>
        {
            ["resource"] = "https://mcp.example.com/mcp",
        };

        await service.RefreshTokenAsync(
            "https://auth.example.com/token",
            "test-client",
            new SensitiveString("old-refresh"),
            extraParams, TestContext.Current.CancellationToken);

        Assert.NotNull(capturedBody);
        Assert.Contains("resource=https%3A%2F%2Fmcp.example.com%2Fmcp", capturedBody);
        Assert.Contains("grant_type=refresh_token", capturedBody);
    }

    [Fact]
    public async Task CompleteAuthorization_WithExtraTokenParams_PassesToExchange()
    {
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            capturedBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(new
            {
                access_token = "at-from-callback",
                expires_in = 3600
            });
        });

        var service = new OAuthPkceService(new HttpClient(handler));

        var extraTokenParams = new Dictionary<string, string>
        {
            ["resource"] = "https://mcp.example.com/mcp",
        };

        var (_, state) = service.StartAuthorizationFlow(
            "https://auth.example.com/authorize",
            "https://auth.example.com/token",
            "test-client",
            "http://127.0.0.1:5199/callback",
            extraTokenParams: extraTokenParams);

        await service.CompleteAuthorizationAsync("callback-code", state, TestContext.Current.CancellationToken);

        Assert.NotNull(capturedBody);
        Assert.Contains("resource=https%3A%2F%2Fmcp.example.com%2Fmcp", capturedBody);
    }
}
