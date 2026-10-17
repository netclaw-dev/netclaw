// -----------------------------------------------------------------------
// <copyright file="ProviderOAuthTokenRefreshServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Providers;
using Netclaw.Providers.OAuth;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Configuration.Tests.Providers.OAuth;

public sealed class ProviderOAuthTokenRefreshServiceTests
{
    private static readonly OAuthAuth OpenAiOAuth = new()
    {
        SupportedAuthMethods = [AuthMethod.OAuthDevice, AuthMethod.OAuthPkce],
        TokenEndpoint = new Uri("https://auth.openai.com/oauth/token"),
        DeviceEndpoint = new Uri("https://auth.openai.com/api/accounts/deviceauth/usercode"),
        ClientId = "client-id",
        UseProprietaryDeviceFlow = true,
    };

    [Fact]
    public async Task GetValidAccessTokenAsync_UnexpiredToken_DoesNotRefresh()
    {
        using var dir = new DisposableTempDir();
        var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var service = CreateService(new NetclawPaths(dir.Path), time, new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("refresh should not be called")));
        var entry = new ProviderEntry
        {
            Type = "openai",
            AuthMethod = AuthMethod.OAuthDevice,
            OAuthAccessToken = new SensitiveString("access-old"),
            OAuthRefreshToken = new SensitiveString("refresh-old"),
            OAuthTokenExpiry = now.AddMinutes(10),
            OAuthAccountId = new SensitiveString("account-old"),
        };

        var token = await service.GetValidAccessTokenAsync(
            "openai-codex", entry, OpenAiOAuth, TestContext.Current.CancellationToken);

        Assert.Equal("access-old", token.Value);
        Assert.Equal("access-old", entry.OAuthAccessToken!.Value);
    }

    [Fact]
    public async Task ForceRefreshAccessTokenAsync_RefreshesEvenWhenTokenIsNotNearExpiry()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var service = CreateService(paths, time, new FakeHttpMessageHandler(_ =>
            FakeHttpMessageHandler.JsonResponse(new
            {
                access_token = "access-new",
                refresh_token = "refresh-new",
                expires_in = 3600,
            })));
        var entry = new ProviderEntry
        {
            Type = "openai",
            AuthMethod = AuthMethod.OAuthDevice,
            OAuthAccessToken = new SensitiveString("access-old"),
            OAuthRefreshToken = new SensitiveString("refresh-old"),
            OAuthTokenExpiry = now.AddHours(1),
        };

        var token = await service.ForceRefreshAccessTokenAsync(
            "openai-codex", entry, OpenAiOAuth, "access-old", TestContext.Current.CancellationToken);

        Assert.Equal("access-new", token.Value);
        Assert.Equal("access-new", entry.OAuthAccessToken!.Value);
    }

    [Fact]
    public async Task GetValidAccessTokenAsync_ExpiredToken_RefreshesPersistsAndUpdatesEntry()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        paths.EnsureDirectoriesExist();
        File.WriteAllText(paths.NetclawConfigPath, """
            {
              "configVersion": 1,
              "Providers": {
                "openai-codex": {
                  "Type": "openai",
                  "AuthMethod": "OAuthDevice"
                }
              }
            }
            """);

        var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var idToken = JwtTestToken.Make(new Dictionary<string, object>
        {
            ["https://api.openai.com/auth"] = new Dictionary<string, object>
            {
                ["chatgpt_account_id"] = "account-new"
            }
        });
        HttpRequestMessage? refreshRequest = null;
        var service = CreateService(paths, time, new FakeHttpMessageHandler(request =>
        {
            refreshRequest = request;
            return FakeHttpMessageHandler.JsonResponse(new
            {
                access_token = "access-new",
                refresh_token = "refresh-new",
                id_token = idToken,
                expires_in = 3600,
            });
        }));
        var entry = new ProviderEntry
        {
            Type = "openai",
            AuthMethod = AuthMethod.OAuthDevice,
            OAuthAccessToken = new SensitiveString("access-old"),
            OAuthRefreshToken = new SensitiveString("refresh-old"),
            OAuthTokenExpiry = now.AddMinutes(-1),
            OAuthAccountId = new SensitiveString("account-old"),
        };

        var token = await service.GetValidAccessTokenAsync(
            "openai-codex", entry, OpenAiOAuth, TestContext.Current.CancellationToken);

        Assert.Equal("access-new", token.Value);
        Assert.Equal("access-new", entry.OAuthAccessToken!.Value);
        Assert.Equal("refresh-new", entry.OAuthRefreshToken!.Value);
        Assert.Equal("account-new", entry.OAuthAccountId!.Value);
        Assert.Equal(now.AddHours(1), entry.OAuthTokenExpiry);

        Assert.NotNull(refreshRequest);
        Assert.Equal(OpenAiOAuth.TokenEndpoint, refreshRequest!.RequestUri);
        var form = await refreshRequest.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("grant_type=refresh_token", form);
        Assert.Contains("refresh_token=refresh-old", form);

        // Refreshed credentials reach disk encrypted. This test asserted the plaintext form
        // before the refresh path was required to supply a protector.
        var rawSecrets = File.ReadAllText(paths.SecretsPath);
        Assert.DoesNotContain("access-new", rawSecrets, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-new", rawSecrets, StringComparison.Ordinal);

        var decrypted = SecretsFileWriter.DecryptJsonLeaves(
            rawSecrets, SecretsProtection.CreateProtector(paths));
        using var secretsDoc = JsonDocument.Parse(decrypted);
        var secretProvider = secretsDoc.RootElement.GetProperty("Providers").GetProperty("openai-codex");
        Assert.Equal("access-new", secretProvider.GetProperty("OAuthAccessToken").GetString());
        Assert.Equal("refresh-new", secretProvider.GetProperty("OAuthRefreshToken").GetString());
        Assert.Equal("account-new", secretProvider.GetProperty("OAuthAccountId").GetString());

        using var configDoc = JsonDocument.Parse(File.ReadAllText(paths.NetclawConfigPath));
        var configProvider = configDoc.RootElement.GetProperty("Providers").GetProperty("openai-codex");
        Assert.Equal("openai", configProvider.GetProperty("Type").GetString());
        Assert.False(configProvider.TryGetProperty("OAuthTokenExpiry", out _));

        Assert.True(OAuthStateStore.TryGetExpiry(paths, "openai-codex", out var storedExpiry));
        Assert.Equal(now.AddHours(1), storedExpiry);
    }

    [Fact]
    public async Task GetValidAccessTokenAsync_InvalidGrant_ThrowsAndLeavesCredentialUnchanged()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var sink = new RecordingNotificationSink();
        var service = CreateService(paths, time, new FakeHttpMessageHandler(_ =>
            FakeHttpMessageHandler.JsonResponse(new { error = "invalid_grant" }, HttpStatusCode.BadRequest)), sink);
        var entry = new ProviderEntry
        {
            Type = "openai",
            AuthMethod = AuthMethod.OAuthDevice,
            OAuthAccessToken = new SensitiveString("access-old"),
            OAuthRefreshToken = new SensitiveString("refresh-old"),
            OAuthTokenExpiry = now.AddMinutes(-1),
            OAuthAccountId = new SensitiveString("account-old"),
        };

        var ex = await Assert.ThrowsAsync<ProviderOAuthRefreshRequiredException>(() =>
            service.GetValidAccessTokenAsync(
                "openai-codex", entry, OpenAiOAuth, TestContext.Current.CancellationToken));

        Assert.Contains("netclaw provider", ex.Message);
        Assert.Equal("access-old", entry.OAuthAccessToken!.Value);
        Assert.Equal("refresh-old", entry.OAuthRefreshToken!.Value);
        Assert.Equal("account-old", entry.OAuthAccountId!.Value);
        Assert.False(File.Exists(paths.SecretsPath));

        var alert = Assert.Single(sink.Alerts);
        Assert.Equal(AlertType.ProviderAuthExpired, alert.Category);
        Assert.Equal("invalid_grant", alert.Context!["reason"]);
    }

    [Fact]
    public async Task GetValidAccessTokenAsync_ExpiredTokenWithoutRefreshToken_ThrowsAndEmitsAlert()
    {
        using var dir = new DisposableTempDir();
        var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var sink = new RecordingNotificationSink();
        var service = CreateService(new NetclawPaths(dir.Path), time, new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("refresh should not be called")), sink);
        var entry = new ProviderEntry
        {
            Type = "openai",
            AuthMethod = AuthMethod.OAuthDevice,
            OAuthAccessToken = new SensitiveString("access-old"),
            OAuthTokenExpiry = now.AddMinutes(-1),
            OAuthAccountId = new SensitiveString("account-old"),
        };

        await Assert.ThrowsAsync<ProviderOAuthRefreshRequiredException>(() =>
            service.GetValidAccessTokenAsync(
                "openai-codex", entry, OpenAiOAuth, TestContext.Current.CancellationToken));

        var alert = Assert.Single(sink.Alerts);
        Assert.Equal(AlertType.ProviderAuthExpired, alert.Category);
        Assert.Equal("no_refresh_token", alert.Context!["reason"]);
    }

    [Fact]
    public async Task GetValidAccessTokenAsync_AnotherProcessRefreshedTheToken_UsesTheStoredTokenWithoutRefreshing()
    {
        using var dir = new DisposableTempDir();
        var paths = NewPaths(dir);
        var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        StoreTokens(paths, "openai-codex", "access-stored", "refresh-stored", now.AddHours(1));
        var service = CreateService(paths, time, new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("refresh should not be called")));
        var entry = ExpiredEntry(now);

        var token = await service.GetValidAccessTokenAsync(
            "openai-codex", entry, OpenAiOAuth, TestContext.Current.CancellationToken);

        Assert.Equal("access-stored", token.Value);
        Assert.Equal("refresh-stored", entry.OAuthRefreshToken!.Value);
        Assert.Equal(now.AddHours(1), entry.OAuthTokenExpiry);
    }

    [Fact]
    public async Task GetValidAccessTokenAsync_StoredRefreshTokenDiffers_SpendsTheStoredRefreshToken()
    {
        using var dir = new DisposableTempDir();
        var paths = NewPaths(dir);
        var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        StoreTokens(paths, "openai-codex", "access-stored", "refresh-stored", now.AddMinutes(-30));
        HttpRequestMessage? refreshRequest = null;
        var service = CreateService(paths, time, new FakeHttpMessageHandler(request =>
        {
            refreshRequest = request;
            return FakeHttpMessageHandler.JsonResponse(new { access_token = "access-new", refresh_token = "refresh-new", expires_in = 3600 });
        }));

        var token = await service.GetValidAccessTokenAsync(
            "openai-codex", ExpiredEntry(now), OpenAiOAuth, TestContext.Current.CancellationToken);

        Assert.Equal("access-new", token.Value);
        var form = await refreshRequest!.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("refresh_token=refresh-stored", form);
        Assert.DoesNotContain("refresh-old", form);
    }

    [Fact]
    public async Task ForceRefreshAccessTokenAsync_AnotherProcessAlreadyReplacedTheRejectedToken_UsesTheStoredToken()
    {
        using var dir = new DisposableTempDir();
        var paths = NewPaths(dir);
        var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        StoreTokens(paths, "openai-codex", "access-stored", "refresh-stored", now.AddHours(1));
        var service = CreateService(paths, time, new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("refresh should not be called")));
        var entry = ExpiredEntry(now);
        entry.OAuthTokenExpiry = now.AddMinutes(30);

        var token = await service.ForceRefreshAccessTokenAsync(
            "openai-codex", entry, OpenAiOAuth, "access-old", TestContext.Current.CancellationToken);

        Assert.Equal("access-stored", token.Value);
    }

    private static NetclawPaths NewPaths(DisposableTempDir dir)
    {
        var paths = new NetclawPaths(dir.Path);
        paths.EnsureDirectoriesExist();
        return paths;
    }

    private static void StoreTokens(NetclawPaths paths, string provider, string access, string refresh, DateTimeOffset expiry)
        => OAuthTokenPersistence.PersistTokens(
            paths, provider, new OAuthDeviceFlowResult(new SensitiveString(access), new SensitiveString(refresh), expiry), new NullSecretsProtector());

    private static ProviderEntry ExpiredEntry(DateTimeOffset now) => new()
    {
        Type = "openai",
        AuthMethod = AuthMethod.OAuthDevice,
        OAuthAccessToken = new SensitiveString("access-old"),
        OAuthRefreshToken = new SensitiveString("refresh-old"),
        OAuthTokenExpiry = now.AddMinutes(-1),
        OAuthAccountId = new SensitiveString("account-old"),
    };

    private static ProviderOAuthTokenRefreshService CreateService(
        NetclawPaths paths,
        FakeTimeProvider time,
        FakeHttpMessageHandler handler,
        IOperationalNotificationSink? sink = null)
    {
        var httpClient = new HttpClient(handler);
        var factory = new DeviceFlowServiceFactory(
            new OAuthDeviceFlowService(httpClient, time),
            new OpenAiDeviceFlowService(httpClient, time));

        return new ProviderOAuthTokenRefreshService(paths, factory, NullLogger<ProviderOAuthTokenRefreshService>.Instance, sink, time);
    }

    private sealed class RecordingNotificationSink : IOperationalNotificationSink
    {
        public List<OperationalAlert> Alerts { get; } = [];
        public void Emit(OperationalAlert alert) => Alerts.Add(alert);
    }
}
