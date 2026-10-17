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

    [Fact]
    public async Task After_a_failed_secrets_write_the_live_token_works_against_the_OpenAI_reuse_answer()
    {
        var server = new TokenServer { OpenAiReuseShape = true };
        var now = _time.GetUtcNow();
        StoreTokens("access-0", "refresh-0", now.AddMinutes(-1));
        var service = CreateService(SecretsWriteFailsAfterTheFirstRefresh(server, out var restoreSecrets));
        var entry = Entry("access-0", "refresh-0", now.AddMinutes(-1));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.GetValidAccessTokenAsync("codex", entry, OpenAiOAuth, TestContext.Current.CancellationToken));
        restoreSecrets();
        _time.Advance(TimeSpan.FromHours(2));

        var token = await service.GetValidAccessTokenAsync("codex", entry, OpenAiOAuth, TestContext.Current.CancellationToken);

        Assert.Equal("access-2", token.Value);
        Assert.Equal(0, server.Reuses);
    }

    // auth.openai.com answers a spent refresh token with {"error":{"code":"refresh_token_reused",...}}.
    [Fact]
    public async Task The_OpenAI_reuse_answer_asks_for_a_new_sign_in_and_raises_the_alert()
    {
        var server = new TokenServer { OpenAiReuseShape = true };
        server.Advance("refresh-1");
        var now = _time.GetUtcNow();
        StoreTokens("access-0", "refresh-0", now.AddMinutes(-1));
        var service = CreateService(new FakeHttpMessageHandler(server.Handle));

        await Assert.ThrowsAsync<ProviderOAuthRefreshRequiredException>(() =>
            service.GetValidAccessTokenAsync("codex", Entry("access-0", "refresh-0", now.AddMinutes(-1)), OpenAiOAuth,
                TestContext.Current.CancellationToken));

        var alert = Assert.Single(_alerts.Alerts);
        Assert.Equal(AlertType.ProviderAuthExpired, alert.Category);
        Assert.Equal(["refresh-0"], server.Presented);
    }

    // The CLI refreshed earlier, so the disk holds refresh-1 and daemon memory holds refresh-0. The CLI
    // refreshes again while the daemon refreshes: it spends refresh-1 and saves refresh-2 first.
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

        var token = await service.GetValidAccessTokenAsync("codex", daemonEntry, OpenAiOAuth,
            TestContext.Current.CancellationToken);

        Assert.Equal("access-2", token.Value);
        Assert.Equal(["refresh-1"], server.Presented);
        Assert.Empty(_alerts.Alerts);
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

        server.FailNextWith500 = true;
        await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            service.GetValidAccessTokenAsync("codex", entry, OpenAiOAuth, TestContext.Current.CancellationToken));

        var token = await service.GetValidAccessTokenAsync("codex", entry, OpenAiOAuth, TestContext.Current.CancellationToken);
        Assert.Equal("access-2", token.Value);
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
    public async Task A_rejected_token_is_not_retried_when_no_other_process_changed_secrets_json()
    {
        var server = new TokenServer();
        server.Advance("refresh-9");
        var now = _time.GetUtcNow();
        StoreTokens("access-0", "refresh-0", now.AddMinutes(-1));
        var service = CreateService(new FakeHttpMessageHandler(server.Handle));

        await Assert.ThrowsAsync<ProviderOAuthRefreshRequiredException>(() =>
            service.GetValidAccessTokenAsync("codex", Entry("access-0", "refresh-1", now.AddMinutes(-1)), OpenAiOAuth,
                TestContext.Current.CancellationToken));

        Assert.Equal(["refresh-0"], server.Presented);
        Assert.Single(_alerts.Alerts);
    }

    private void StoreTokens(string access, string refresh, DateTimeOffset expiry)
        => OAuthTokenPersistence.PersistTokens(_paths, "codex",
            new OAuthDeviceFlowResult(new SensitiveString(access), new SensitiveString(refresh), expiry),
            SecretsProtection.CreateProtector(_paths));

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

        public bool Rotate { get; init; } = true;

        public bool RevokeFamilyOnReuse { get; init; }

        public bool OpenAiReuseShape { get; init; }

        public bool FailNextWith500 { get; set; }

        public int Reuses { get; private set; }

        public List<string> Presented { get; } = [];

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
                        if (OpenAiReuseShape)
                        {
                            return FakeHttpMessageHandler.JsonResponse(new
                            {
                                error = new
                                {
                                    message = "Your refresh token has already been used to generate a new access token. Please try signing in again.",
                                    type = "invalid_request_error",
                                    code = "refresh_token_reused",
                                },
                            }, HttpStatusCode.Unauthorized);
                        }
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

        public void Emit(OperationalAlert alert) => Alerts.Add(alert);
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
