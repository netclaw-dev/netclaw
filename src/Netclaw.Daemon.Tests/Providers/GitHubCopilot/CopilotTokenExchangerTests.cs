// -----------------------------------------------------------------------
// <copyright file="CopilotTokenExchangerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Providers.GitHubCopilot;
using Netclaw.Providers.OAuth;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers.GitHubCopilot;

public sealed class CopilotTokenExchangerTests
{
    private static readonly OAuthAuth PublicOAuth = new()
    {
        SupportedAuthMethods = [AuthMethod.OAuthDevice],
        TokenEndpoint = new Uri("https://github.com/login/oauth/access_token"),
        DeviceEndpoint = new Uri("https://github.com/login/device/code"),
        ClientId = "copilot-client",
        Scope = "read:user",
        UseProprietaryDeviceFlow = false,
    };

    private static ProviderEntry EntryWithOAuth(string token) =>
        new()
        {
            Type = "github-copilot",
            AuthMethod = AuthMethod.OAuthDevice,
            OAuthAccessToken = new SensitiveString(token),
        };

    private static ProviderEntry ExpiredEntryWithOAuth(
        DateTimeOffset now,
        string accessToken = "oauth-old",
        string? refreshToken = "refresh-old") =>
        new()
        {
            Type = "github-copilot",
            AuthMethod = AuthMethod.OAuthDevice,
            OAuthAccessToken = new SensitiveString(accessToken),
            OAuthRefreshToken = refreshToken is null ? null : new SensitiveString(refreshToken),
            OAuthTokenExpiry = now.AddMinutes(-1),
        };

    private static HttpResponseMessage TokenResponse(string token, long expiresAt) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { token, expires_at = expiresAt }),
                Encoding.UTF8,
                "application/json"),
        };

    [Fact]
    public async Task GetToken_FirstCall_FetchesAndCaches()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            return TokenResponse("copilot-token-1",
                DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds());
        });
        var exchanger = new CopilotTokenExchanger(new HttpClient(handler));

        var token = await exchanger.GetTokenAsync(EntryWithOAuth("oauth-1"),
            TestContext.Current.CancellationToken);

        Assert.Equal("copilot-token-1", token);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GetToken_NamedProvider_ExpiredOAuthToken_RefreshesBeforeTokenExchange()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        WriteProviderConfig(paths, "copilot");

        var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var requestUris = new List<string>();
        string? exchangeAuthorization = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            requestUris.Add(request.RequestUri!.ToString());
            return request.RequestUri!.ToString() switch
            {
                "https://github.com/login/oauth/access_token" => FakeHttpMessageHandler.JsonResponse(new
                {
                    access_token = "oauth-new",
                    refresh_token = "refresh-new",
                    expires_in = 3600,
                }),
                "https://api.github.com/copilot_internal/v2/token" => CaptureExchangeRequest(request),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });
        var service = CreateRefreshService(paths, time, handler);
        var exchanger = new CopilotTokenExchanger(new HttpClient(handler), time, service);
        var entry = ExpiredEntryWithOAuth(now);

        var token = await exchanger.GetTokenAsync(
            "copilot",
            entry,
            PublicOAuth,
            TestContext.Current.CancellationToken);

        Assert.Equal("copilot-token", token);
        Assert.Equal("oauth-new", entry.OAuthAccessToken!.Value);
        Assert.Equal("refresh-new", entry.OAuthRefreshToken!.Value);
        Assert.Equal(now.AddHours(1), entry.OAuthTokenExpiry);
        Assert.Equal("Bearer oauth-new", exchangeAuthorization);
        Assert.Equal([
            "https://github.com/login/oauth/access_token",
            "https://api.github.com/copilot_internal/v2/token",
        ], requestUris);

        using var secretsDoc = JsonDocument.Parse(File.ReadAllText(paths.SecretsPath));
        var secretProvider = secretsDoc.RootElement.GetProperty("Providers").GetProperty("copilot");
        Assert.Equal("oauth-new", secretProvider.GetProperty("OAuthAccessToken").GetString());
        Assert.Equal("refresh-new", secretProvider.GetProperty("OAuthRefreshToken").GetString());

        using var configDoc = JsonDocument.Parse(File.ReadAllText(paths.NetclawConfigPath));
        var configProvider = configDoc.RootElement.GetProperty("Providers").GetProperty("copilot");
        Assert.Equal(now.AddHours(1), DateTimeOffset.Parse(configProvider.GetProperty("OAuthTokenExpiry").GetString()!));

        HttpResponseMessage CaptureExchangeRequest(HttpRequestMessage request)
        {
            exchangeAuthorization = request.Headers.Authorization!.ToString();
            return TokenResponse("copilot-token", now.AddMinutes(30).ToUnixTimeSeconds());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetToken_NamedProvider_RefreshFailure_ThrowsBeforeTokenExchange(bool hasRefreshToken)
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var handler = new FakeHttpMessageHandler(request =>
            request.RequestUri!.ToString() switch
            {
                "https://github.com/login/oauth/access_token" when hasRefreshToken => FakeHttpMessageHandler.JsonResponse(
                    new { error = "invalid_grant" }, HttpStatusCode.BadRequest),
                "https://api.github.com/copilot_internal/v2/token" => throw new InvalidOperationException(
                    "token exchange should not run after failed refresh"),
                _ => throw new InvalidOperationException("unexpected HTTP request"),
            });
        var service = CreateRefreshService(paths, time, handler);
        var exchanger = new CopilotTokenExchanger(new HttpClient(handler), time, service);

        var ex = await Assert.ThrowsAsync<ProviderOAuthRefreshRequiredException>(() =>
            exchanger.GetTokenAsync(
                "copilot",
                ExpiredEntryWithOAuth(now, refreshToken: hasRefreshToken ? "refresh-old" : null),
                PublicOAuth,
                TestContext.Current.CancellationToken));

        Assert.Contains("provider fix copilot", ex.Message);
    }

    [Theory]
    [InlineData(25, "copilot-token-1", 1)]
    [InlineData(29, "copilot-token-2", 2)]
    public async Task GetToken_UsesRefreshBuffer(int advanceMinutes, string expectedToken, int expectedCalls)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            return TokenResponse(
                $"copilot-token-{callCount}",
                time.GetUtcNow().AddMinutes(30).ToUnixTimeSeconds());
        });
        var exchanger = new CopilotTokenExchanger(new HttpClient(handler), time);

        var entry = EntryWithOAuth("oauth-1");
        await exchanger.GetTokenAsync(entry, TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromMinutes(advanceMinutes));
        var token = await exchanger.GetTokenAsync(entry, TestContext.Current.CancellationToken);

        Assert.Equal(expectedToken, token);
        Assert.Equal(expectedCalls, callCount);
    }

    [Fact]
    public async Task GetToken_DistinctOAuthTokens_CacheSeparately()
    {
        var requestsByToken = new Dictionary<string, int>();
        var handler = new FakeHttpMessageHandler(request =>
        {
            var auth = request.Headers.GetValues("Authorization").First();
            requestsByToken[auth] = requestsByToken.GetValueOrDefault(auth) + 1;
            return TokenResponse($"copilot-for-{auth}",
                DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds());
        });
        var exchanger = new CopilotTokenExchanger(new HttpClient(handler));

        await exchanger.GetTokenAsync(EntryWithOAuth("oauth-A"),
            TestContext.Current.CancellationToken);
        await exchanger.GetTokenAsync(EntryWithOAuth("oauth-B"),
            TestContext.Current.CancellationToken);
        await exchanger.GetTokenAsync(EntryWithOAuth("oauth-A"),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, requestsByToken["Bearer oauth-A"]);
        Assert.Equal(1, requestsByToken["Bearer oauth-B"]);
    }

    [Fact]
    public async Task GetToken_Unauthorized_ThrowsCopilotAuthExpired()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var exchanger = new CopilotTokenExchanger(new HttpClient(handler));

        await Assert.ThrowsAsync<CopilotAuthExpiredException>(() =>
            exchanger.GetTokenAsync(EntryWithOAuth("oauth-revoked"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetToken_ServerError_ThrowsInvalidOperationWithDetail()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("upstream offline", Encoding.UTF8, "text/plain"),
        });
        var exchanger = new CopilotTokenExchanger(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            exchanger.GetTokenAsync(EntryWithOAuth("oauth-1"),
                TestContext.Current.CancellationToken));

        Assert.Contains("HTTP 502", ex.Message);
        Assert.Contains("upstream offline", ex.Message);
    }

    [Fact]
    public async Task GetToken_MissingOAuthToken_Throws()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            TokenResponse("never-called", DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds()));
        var exchanger = new CopilotTokenExchanger(new HttpClient(handler));

        var entry = new ProviderEntry
        {
            Type = "github-copilot",
            AuthMethod = AuthMethod.OAuthDevice,
            OAuthAccessToken = null,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            exchanger.GetTokenAsync(entry, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetToken_TokenRequest_SendsEditorIntegrationContract()
    {
        // The exchange endpoint gates on the editor-integration header set —
        // Copilot-Integration-Id in particular is what makes GitHub's gateway
        // route through the Copilot permission model. Missing any of these
        // headers returns HTTP 403 "Resource not accessible by integration"
        // even with a valid OAuth-App user-to-server token. Lock this set in
        // so a future cleanup pass doesn't silently regress the integration.
        HttpRequestMessage? captured = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            captured = request;
            return TokenResponse("copilot",
                DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds());
        });
        var exchanger = new CopilotTokenExchanger(new HttpClient(handler));

        await exchanger.GetTokenAsync(EntryWithOAuth("ghu_oauth"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("https://api.github.com/copilot_internal/v2/token",
            captured!.RequestUri!.ToString());
        Assert.Equal("Bearer ghu_oauth",
            captured.Headers.GetValues("Authorization").Single());
        Assert.Contains(captured.Headers.Accept, h => h.MediaType == "application/json");

        Assert.Equal(NetclawUserAgent.Value,
            string.Join(" ", captured.Headers.GetValues("User-Agent")));
        Assert.Equal("copilot-token",
            captured.Headers.GetValues(NetclawUserAgent.ComponentHeader).Single());

        Assert.Equal($"Netclaw/{BuildInfo.Version}",
            captured.Headers.GetValues("Editor-Version").Single());
        Assert.Equal($"netclaw/{BuildInfo.Version}",
            captured.Headers.GetValues("Editor-Plugin-Version").Single());
        Assert.Equal("vscode-chat", captured.Headers.GetValues("Copilot-Integration-Id").Single());
        Assert.Equal("2022-11-28", captured.Headers.GetValues("X-GitHub-Api-Version").Single());
    }

    private static ProviderOAuthTokenRefreshService CreateRefreshService(
        NetclawPaths paths,
        FakeTimeProvider time,
        FakeHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        var factory = new DeviceFlowServiceFactory(
            new OAuthDeviceFlowService(httpClient, time),
            new OpenAiDeviceFlowService(httpClient, time));

        return new ProviderOAuthTokenRefreshService(paths, factory, timeProvider: time);
    }

    private static void WriteProviderConfig(NetclawPaths paths, string providerName)
    {
        paths.EnsureDirectoriesExist();
        File.WriteAllText(paths.NetclawConfigPath, $$"""
            {
              "configVersion": 1,
              "Providers": {
                "{{providerName}}": {
                  "Type": "github-copilot",
                  "AuthMethod": "OAuthDevice"
                }
              }
            }
            """);
    }
}
