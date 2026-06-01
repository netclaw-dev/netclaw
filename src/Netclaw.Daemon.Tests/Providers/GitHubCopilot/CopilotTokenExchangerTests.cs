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
using Netclaw.Providers.GitHubCopilot;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers.GitHubCopilot;

public sealed class CopilotTokenExchangerTests
{
    private static ProviderEntry EntryWithOAuth(string token) =>
        new()
        {
            Type = "github-copilot",
            AuthMethod = AuthMethod.OAuthDevice,
            OAuthAccessToken = new SensitiveString(token),
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
    public async Task GetToken_WithinTtl_ReturnsCachedWithoutHttp()
    {
        var callCount = 0;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            return TokenResponse("copilot-token-1",
                time.GetUtcNow().AddMinutes(30).ToUnixTimeSeconds());
        });
        var exchanger = new CopilotTokenExchanger(new HttpClient(handler), time);

        var entry = EntryWithOAuth("oauth-1");
        await exchanger.GetTokenAsync(entry, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(5));
        await exchanger.GetTokenAsync(entry, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(20)); // still > 2 min before expiry
        var third = await exchanger.GetTokenAsync(entry, TestContext.Current.CancellationToken);

        Assert.Equal("copilot-token-1", third);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GetToken_WithinRefreshBuffer_FetchesFreshToken()
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

        // Advance to inside the 2-minute refresh buffer (29 minutes elapsed → 1 minute to expiry)
        time.Advance(TimeSpan.FromMinutes(29));
        var refreshed = await exchanger.GetTokenAsync(entry, TestContext.Current.CancellationToken);

        Assert.Equal("copilot-token-2", refreshed);
        Assert.Equal(2, callCount);
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
}
