// -----------------------------------------------------------------------
// <copyright file="CrossProcessOAuthRefreshTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Providers;
using Netclaw.Providers.OAuth;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

// `netclaw provider` probes each configured provider and refreshes an expired OAuth token. That
// refresh rotates the refresh token while the daemon runs. The daemon must spend the rotated token.
// A save of refreshed tokens can also fail. Then secrets.json holds a spent token and the daemon holds
// the live one in memory. A server with reuse detection (RFC 9700 4.14.2) revokes the whole token
// family when a spent token comes back, so the daemon must never present one.
public sealed class CrossProcessOAuthRefreshTests : IDisposable
{
    private static readonly OAuthAuth OpenAiOAuth = new()
    {
        SupportedAuthMethods = [AuthMethod.OAuthDevice, AuthMethod.OAuthPkce],
        TokenEndpoint = new Uri("https://auth.openai.com/oauth/token"),
        DeviceEndpoint = new Uri("https://auth.openai.com/api/accounts/deviceauth/usercode"),
        ClientId = "client-id",
        UseProprietaryDeviceFlow = true,
    };

    // The standard flow of GitHub and other public clients. The token request goes to
    // OAuthDeviceFlowService, which keeps the old refresh token itself only when the caller does.
    private static readonly OAuthAuth StandardOAuth = new()
    {
        SupportedAuthMethods = [AuthMethod.OAuthDevice],
        TokenEndpoint = new Uri("https://github.example/login/oauth/access_token"),
        DeviceEndpoint = new Uri("https://github.example/login/device/code"),
        ClientId = "client-id",
        UseProprietaryDeviceFlow = false,
    };

    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly CapturingLogger _logger = new();
    private readonly RecordingNotificationSink _alerts = new();

    public CrossProcessOAuthRefreshTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    [Fact]
    public async Task A_refresh_by_the_CLI_reaches_the_running_daemon()
    {
        var server = new TokenServer();
        var now = _time.GetUtcNow();
        StoreTokens("access-0", "refresh-0", now.AddMinutes(-1));
        var handler = new FakeHttpMessageHandler(server.Handle);

        // The daemon bound its entry at startup, before the CLI refreshed.
        var daemonEntry = Entry("access-0", "refresh-0", now.AddMinutes(-1));
        var daemonService = CreateService(handler);

        var cliService = CreateService(handler);
        await cliService.GetValidAccessTokenAsync("codex",
            Entry("access-0", "refresh-0", now.AddMinutes(-1)), OpenAiOAuth, TestContext.Current.CancellationToken);

        var token = await daemonService.GetValidAccessTokenAsync("codex", daemonEntry, OpenAiOAuth,
            TestContext.Current.CancellationToken);

        Assert.Equal("access-1", token.Value);
        Assert.Equal(["refresh-0"], server.Presented);
    }

    [Fact]
    public async Task With_reuse_detection_a_failed_secrets_write_keeps_the_provider_signed_in()
    {
        var server = new TokenServer { RevokeFamilyOnReuse = true };
        var now = _time.GetUtcNow();
        StoreTokens("access-0", "refresh-0", now.AddMinutes(-1));
        var service = CreateService(SecretsWriteFailsAfterTheFirstRefresh(server, out var restoreSecrets));
        var entry = Entry("access-0", "refresh-0", now.AddMinutes(-1));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.GetValidAccessTokenAsync("codex", entry, OpenAiOAuth, TestContext.Current.CancellationToken));
        restoreSecrets();
        Assert.Equal("refresh-1", entry.OAuthRefreshToken!.Value);

        _time.Advance(TimeSpan.FromHours(2));
        var token = await service.GetValidAccessTokenAsync("codex", entry, OpenAiOAuth, TestContext.Current.CancellationToken);

        Assert.Equal("access-2", token.Value);
        Assert.Equal(["refresh-0", "refresh-1"], server.Presented);
        Assert.Equal(0, server.Reuses);
        Assert.Equal("refresh-2", OAuthTokenPersistence.LoadTokens(_paths, "codex")!.RefreshToken!.Value);
    }

    // `netclaw provider` rebuilds its provider entries from disk, so the entry it passes next holds the
    // spent token from secrets.json.
    [Fact]
    public async Task After_a_failed_secrets_write_an_entry_reloaded_from_disk_still_presents_the_live_token()
    {
        var server = new TokenServer { RevokeFamilyOnReuse = true };
        var now = _time.GetUtcNow();
        StoreTokens("access-0", "refresh-0", now.AddMinutes(-1));
        var service = CreateService(SecretsWriteFailsAfterTheFirstRefresh(server, out var restoreSecrets));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.GetValidAccessTokenAsync("codex", Entry("access-0", "refresh-0", now.AddMinutes(-1)), OpenAiOAuth,
                TestContext.Current.CancellationToken));
        restoreSecrets();
        _time.Advance(TimeSpan.FromHours(2));

        var token = await service.GetValidAccessTokenAsync("codex", Entry("access-0", "refresh-0", now.AddMinutes(-1)),
            OpenAiOAuth, TestContext.Current.CancellationToken);

        Assert.Equal("access-2", token.Value);
        Assert.Equal(["refresh-0", "refresh-1"], server.Presented);
        Assert.Equal(0, server.Reuses);
    }

    // The CLI refreshed earlier, so the disk holds refresh-1 and daemon memory holds refresh-0. The CLI
    // refreshes again while the daemon refreshes: it spends refresh-1 and saves refresh-2 first. The
    // daemon presents the stored refresh-1, never the older refresh-0. The server rejects the spent
    // refresh-1, and the next call adopts the refresh-2 that the CLI saved.
    [Fact]
    public async Task A_concurrent_CLI_refresh_never_makes_the_daemon_present_an_older_token()
    {
        var server = new TokenServer();
        var now = _time.GetUtcNow();
        server.Advance("refresh-1");
        StoreTokens("access-1", "refresh-1", now.AddMinutes(-1));
        var cliWrote = false;
        var handler = new FakeHttpMessageHandler(request =>
        {
            if (!cliWrote)
            {
                cliWrote = true;
                server.Advance("refresh-2");
                StoreTokens("access-2", "refresh-2", now.AddHours(1));
            }

            return server.Handle(request);
        });
        var service = CreateService(handler);
        var daemonEntry = Entry("access-0", "refresh-0", now.AddMinutes(-1));

        await Assert.ThrowsAsync<ProviderOAuthRefreshRequiredException>(() =>
            service.GetValidAccessTokenAsync("codex", daemonEntry, OpenAiOAuth, TestContext.Current.CancellationToken));
        Assert.Equal(["refresh-1"], server.Presented);

        var token = await service.GetValidAccessTokenAsync("codex", daemonEntry, OpenAiOAuth,
            TestContext.Current.CancellationToken);

        Assert.Equal("access-2", token.Value);
        Assert.Equal(["refresh-1"], server.Presented);
    }

    [Fact]
    public async Task One_transient_failure_after_a_failed_write_does_not_lose_the_live_token()
    {
        var server = new TokenServer();
        var now = _time.GetUtcNow();
        StoreTokens("access-0", "refresh-0", now.AddMinutes(-1));
        var service = CreateService(SecretsWriteFailsAfterTheFirstRefresh(server, out var restoreSecrets));
        var entry = Entry("access-0", "refresh-0", now.AddMinutes(-1));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.GetValidAccessTokenAsync("codex", entry, OpenAiOAuth, TestContext.Current.CancellationToken));
        restoreSecrets();
        _time.Advance(TimeSpan.FromHours(2));

        // The 500 gets one retry. Both requests present the live token, never the spent one.
        server.FailNextWith500 = true;
        var token = await FakeClockDriver.AdvanceUntilCompleteAsync(
            service.GetValidAccessTokenAsync("codex", entry, OpenAiOAuth, TestContext.Current.CancellationToken),
            () => _time.Advance(TimeSpan.FromSeconds(2)));
        Assert.Equal("access-2", token.Value);
        Assert.Equal(["refresh-0", "refresh-1", "refresh-1"], server.Presented);
    }

    [Fact]
    public async Task A_failed_secrets_write_after_a_good_refresh_is_logged_as_an_error_with_the_path()
    {
        var server = new TokenServer();
        var now = _time.GetUtcNow();
        StoreTokens("access-0", "refresh-0", now.AddMinutes(-1));
        var service = CreateService(SecretsWriteFailsAfterTheFirstRefresh(server, out _));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.GetValidAccessTokenAsync("codex", Entry("access-0", "refresh-0", now.AddMinutes(-1)), OpenAiOAuth,
                TestContext.Current.CancellationToken));

        var error = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Error);
        Assert.NotNull(error.Exception);
        Assert.Contains(_paths.SecretsPath, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_that_does_not_rotate_gets_one_presentation_per_refresh()
    {
        var server = new TokenServer { Rotate = false };
        var now = _time.GetUtcNow();
        StoreTokens("access-0", "refresh-0", now.AddMinutes(-1));
        var service = CreateService(new FakeHttpMessageHandler(server.Handle));
        var entry = Entry("access-0", "refresh-0", now.AddMinutes(-1));

        for (var i = 0; i < 3; i++)
        {
            await service.GetValidAccessTokenAsync("codex", entry, OpenAiOAuth, TestContext.Current.CancellationToken);
            _time.Advance(TimeSpan.FromHours(2));
        }

        Assert.Equal(["refresh-0", "refresh-0", "refresh-0"], server.Presented);
        Assert.Equal("refresh-0", OAuthTokenPersistence.LoadTokens(_paths, "codex")!.RefreshToken!.Value);
    }

    [Fact]
    public async Task An_older_secrets_json_copy_does_not_bring_back_a_spent_token()
    {
        var server = new TokenServer { RevokeFamilyOnReuse = true };
        var now = _time.GetUtcNow();
        StoreTokens("access-0", "refresh-0", now.AddMinutes(-1));
        var olderCopy = File.ReadAllBytes(_paths.SecretsPath);
        var service = CreateService(new FakeHttpMessageHandler(server.Handle));

        for (var i = 0; i < 2; i++)
        {
            await service.GetValidAccessTokenAsync("codex", Entry("access-0", "refresh-0", now.AddMinutes(-1)), OpenAiOAuth,
                TestContext.Current.CancellationToken);
            _time.Advance(TimeSpan.FromHours(2));
        }

        File.WriteAllBytes(_paths.SecretsPath, olderCopy);
        var token = await service.GetValidAccessTokenAsync("codex", Entry("access-0", "refresh-0", now.AddMinutes(-1)),
            OpenAiOAuth, TestContext.Current.CancellationToken);

        Assert.Equal("access-3", token.Value);
        Assert.Equal(["refresh-0", "refresh-1", "refresh-2"], server.Presented);
        Assert.Equal(0, server.Reuses);
        Assert.Equal("refresh-3", StoredRefresh("codex"));
    }

    [Fact]
    public async Task Two_providers_that_refresh_together_keep_each_others_newest_token_on_disk()
    {
        var serverA = new TokenServer { RevokeFamilyOnReuse = true };
        var serverB = new TokenServer { RevokeFamilyOnReuse = true };
        var oauthA = OAuthFor("https://a.example/token");
        var oauthB = OAuthFor("https://b.example/token");
        var now = _time.GetUtcNow();
        StoreTokens("codex-a", "access-0", "refresh-0", now.AddMinutes(-1));
        StoreTokens("codex-b", "access-0", "refresh-0", now.AddMinutes(-1));

        using var savesStartTogether = new Barrier(2);
        var service = CreateService(new FakeHttpMessageHandler(request =>
        {
            var response = request.RequestUri!.Host == "a.example" ? serverA.Handle(request) : serverB.Handle(request);
            savesStartTogether.SignalAndWait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            return response;
        }));
        var entryA = Entry("access-0", "refresh-0", now.AddMinutes(-1));
        var entryB = Entry("access-0", "refresh-0", now.AddMinutes(-1));

        var lostUpdates = 0;
        for (var round = 0; round < 100 && lostUpdates == 0; round++)
        {
            await Task.WhenAll(
                Task.Run(() => service.GetValidAccessTokenAsync("codex-a", entryA, oauthA, TestContext.Current.CancellationToken)),
                Task.Run(() => service.GetValidAccessTokenAsync("codex-b", entryB, oauthB, TestContext.Current.CancellationToken)));

            if (StoredRefresh("codex-a") != serverA.Current || StoredRefresh("codex-b") != serverB.Current)
                lostUpdates++;
            _time.Advance(TimeSpan.FromHours(2));
        }

        Assert.Equal(0, lostUpdates);
        Assert.Equal(0, serverA.Reuses + serverB.Reuses);
    }

    // Write-back proof. A server that sends no new refresh token keeps the old one valid, but a failed
    // save left refresh-1 in memory and refresh-0 on disk. The next good refresh must write refresh-1 to
    // secrets.json. Without that write, the file keeps the spent refresh-0, and a restarted process
    // presents it. OpenAiDeviceFlowService keeps the old token itself, so this test uses the standard flow.
    [Fact]
    public async Task A_refresh_without_a_new_refresh_token_saves_the_live_token_over_a_spent_one()
    {
        var server = new TokenServer { RevokeFamilyOnReuse = true };
        var now = _time.GetUtcNow();
        StoreTokens("access-0", "refresh-0", now.AddMinutes(-1));
        var service = CreateService(SecretsWriteFailsAfterTheFirstRefresh(server, out var restoreSecrets));
        var entry = Entry("access-0", "refresh-0", now.AddMinutes(-1));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.GetValidAccessTokenAsync("codex", entry, StandardOAuth, TestContext.Current.CancellationToken));
        restoreSecrets();
        server.Rotate = false;
        _time.Advance(TimeSpan.FromHours(2));

        await service.GetValidAccessTokenAsync("codex", entry, StandardOAuth, TestContext.Current.CancellationToken);

        Assert.Equal(["refresh-0", "refresh-1"], server.Presented);
        Assert.Equal("refresh-1", StoredRefresh("codex"));

        // A restarted process builds a new service and a new entry from the file.
        var restarted = CreateService(new FakeHttpMessageHandler(server.Handle));
        var stored = OAuthTokenPersistence.LoadTokens(_paths, "codex")!;
        var restartedEntry = Entry(stored.AccessToken.Value, stored.RefreshToken!.Value, stored.ExpiresAt);
        _time.Advance(TimeSpan.FromHours(2));

        await restarted.GetValidAccessTokenAsync("codex", restartedEntry, StandardOAuth, TestContext.Current.CancellationToken);

        Assert.Equal(0, server.Reuses);
    }

    private void StoreTokens(string access, string refresh, DateTimeOffset expiry)
        => StoreTokens("codex", access, refresh, expiry);

    private void StoreTokens(string provider, string access, string refresh, DateTimeOffset expiry)
        => OAuthTokenPersistence.PersistTokens(_paths, provider,
            new OAuthDeviceFlowResult(new SensitiveString(access), new SensitiveString(refresh), expiry),
            SecretsProtection.CreateProtector(_paths));

    private string? StoredRefresh(string provider)
        => OAuthTokenPersistence.LoadTokens(_paths, provider)?.RefreshToken?.Value;

    private static OAuthAuth OAuthFor(string tokenEndpoint) => new()
    {
        SupportedAuthMethods = OpenAiOAuth.SupportedAuthMethods,
        TokenEndpoint = new Uri(tokenEndpoint),
        DeviceEndpoint = OpenAiOAuth.DeviceEndpoint,
        ClientId = OpenAiOAuth.ClientId,
        UseProprietaryDeviceFlow = true,
    };

    // After the token server answers, a directory takes the place of secrets.json, so the save that
    // follows fails on every platform. The returned action puts the old file back, as it was on disk.
    private FakeHttpMessageHandler SecretsWriteFailsAfterTheFirstRefresh(TokenServer server, out Action restoreSecrets)
    {
        var secretsBeforeRefresh = File.ReadAllBytes(_paths.SecretsPath);
        var broken = false;
        restoreSecrets = () =>
        {
            Directory.Delete(_paths.SecretsPath);
            File.WriteAllBytes(_paths.SecretsPath, secretsBeforeRefresh);
        };

        return new FakeHttpMessageHandler(request =>
        {
            var response = server.Handle(request);
            if (!broken)
            {
                broken = true;
                File.Delete(_paths.SecretsPath);
                Directory.CreateDirectory(_paths.SecretsPath);
            }

            return response;
        });
    }

    private static ProviderEntry Entry(string access, string refresh, DateTimeOffset? expiry) => new()
    {
        Type = "openai",
        AuthMethod = AuthMethod.OAuthDevice,
        OAuthAccessToken = new SensitiveString(access),
        OAuthRefreshToken = new SensitiveString(refresh),
        OAuthTokenExpiry = expiry,
        OAuthAccountId = new SensitiveString("account"),
    };

    private ProviderOAuthTokenRefreshService CreateService(FakeHttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        var factory = new DeviceFlowServiceFactory(new OAuthDeviceFlowService(http, _time), new OpenAiDeviceFlowService(http, _time));
        return new ProviderOAuthTokenRefreshService(_paths, factory, _logger, _alerts, _time);
    }

    public void Dispose() => _dir.Dispose();

    // A rotating token server: each refresh token works once (RFC 6819 5.2.2.3).
    private sealed class TokenServer
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _spent = new(StringComparer.Ordinal);
        private string? _current = "refresh-0";
        private int _generation;

        public bool Rotate { get; set; } = true;

        public bool RevokeFamilyOnReuse { get; init; }

        public bool FailNextWith500 { get; set; }

        public int Reuses { get; private set; }

        public List<string> Presented { get; } = [];

        public string? Current
        {
            get
            {
                lock (_gate)
                    return _current;
            }
        }

        // Another client spent the current token and got `next`.
        public void Advance(string next)
        {
            lock (_gate)
            {
                if (_current is not null)
                    _spent.Add(_current);
                _current = next;
                _generation = int.Parse(next["refresh-".Length..]);
            }
        }

        public HttpResponseMessage Handle(HttpRequestMessage request)
        {
            var form = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var presented = Regex.Match(form, "refresh_token=([^&]+)").Groups[1].Value;
            lock (_gate)
            {
                Presented.Add(presented);
                if (FailNextWith500)
                {
                    FailNextWith500 = false;
                    return FakeHttpMessageHandler.JsonResponse(new { error = "server_error" }, HttpStatusCode.InternalServerError);
                }

                if (!string.Equals(presented, _current, StringComparison.Ordinal))
                {
                    if (_spent.Contains(presented))
                    {
                        Reuses++;
                        if (RevokeFamilyOnReuse)
                            _current = null;
                    }

                    return FakeHttpMessageHandler.JsonResponse(new { error = "invalid_grant" }, HttpStatusCode.BadRequest);
                }

                if (!Rotate)
                {
                    return FakeHttpMessageHandler.JsonResponse(new
                    {
                        access_token = $"access-{++_generation}",
                        expires_in = 3600,
                    });
                }

                _spent.Add(_current!);
                _generation++;
                _current = $"refresh-{_generation}";
                return FakeHttpMessageHandler.JsonResponse(new
                {
                    access_token = $"access-{_generation}",
                    refresh_token = _current,
                    expires_in = 3600,
                });
            }
        }
    }

    private sealed class RecordingNotificationSink : IOperationalNotificationSink
    {
        public List<OperationalAlert> Alerts { get; } = [];

        public void Emit(OperationalAlert alert)
        {
            lock (Alerts)
                Alerts.Add(alert);
        }
    }

    private sealed class CapturingLogger : ILogger<ProviderOAuthTokenRefreshService>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
