// -----------------------------------------------------------------------
// <copyright file="MattermostFixture.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Netclaw.Channels.Mattermost.Bootstrap;
using Xunit;

namespace Netclaw.Channels.Mattermost.IntegrationTests;

/// <summary>
/// Manages a real Mattermost server container for integration testing.
/// Creates admin user, bot account with token, test team, channel, and test user.
/// </summary>
public sealed class MattermostFixture : IAsyncLifetime
{
    private static readonly BootstrapOptions SeedOptions = new();

    private IContainer? _container;
    private string? _testUserToken;

    public string ServerUrl { get; private set; } = string.Empty;
    public string AdminToken { get; private set; } = string.Empty;
    public string BotToken { get; private set; } = string.Empty;
    public string BotUserId { get; private set; } = string.Empty;
    public string TeamId { get; private set; } = string.Empty;
    public string ChannelId { get; private set; } = string.Empty;
    public string TestUserId { get; private set; } = string.Empty;

    /// <summary>
    /// Set when no Docker/container runtime is available, so every test in the
    /// collection self-skips instead of failing. Null when the container started.
    /// </summary>
    public string? SkipReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        // Opt-in by design: these integration tests stand up a real Mattermost
        // container via Testcontainers and run a multi-second handshake against
        // it. They are excluded from required CI (PR description, task 14.5) and
        // only execute when the developer (or a dedicated CI lane) explicitly
        // sets NETCLAW_RUN_MATTERMOST_INTEGRATION_TESTS=1. Default CI sees the
        // env var unset and every test in the collection self-skips.
        var optIn = Environment.GetEnvironmentVariable("NETCLAW_RUN_MATTERMOST_INTEGRATION_TESTS");
        if (!string.Equals(optIn, "1", StringComparison.Ordinal))
        {
            SkipReason = "Mattermost integration tests are opt-in; set NETCLAW_RUN_MATTERMOST_INTEGRATION_TESTS=1 to run.";
            return;
        }

        IContainer? container = null;
        try
        {
            // Both the builder chain and StartAsync can surface a
            // Docker-unavailable error, so both run inside the try.
            var builder = new ContainerBuilder()
                .WithImage("mattermost/mattermost-preview")
                .WithPortBinding(8065, true);

            foreach (var (name, value) in MattermostBootstrapper.DefaultEnvironmentVariables)
                builder = builder.WithEnvironment(name, value);

            container = builder
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(r => r
                        .ForPort(8065)
                        .ForPath("/api/v4/system/ping")
                        .ForStatusCode(HttpStatusCode.OK))
                    .AddCustomWaitStrategy(new WaitUntilApiReady()))
                .Build();

            await container.StartAsync();
        }
        catch (Exception ex) when (IsDockerUnavailable(ex))
        {
            // No Docker/container runtime on this host (e.g. the Windows and
            // macOS CI runners). These integration tests are best-effort and
            // must never block CI — every test in the collection self-skips.
            if (container is not null)
                await container.DisposeAsync();
            SkipReason = $"Docker is not available; Mattermost integration tests skipped. ({ex.GetType().Name}: {ex.Message})";
            return;
        }

        _container = container;
        var port = _container.GetMappedPublicPort(8065);
        ServerUrl = $"http://localhost:{port}";

        // All REST seeding (admin/login/team/bot/token/channel/test user)
        // lives in MattermostBootstrapper so the demo AppHost and this
        // fixture share the same code path.
        var result = await MattermostBootstrapper.SeedAsync(
            new Uri(ServerUrl),
            SeedOptions,
            CancellationToken.None);

        AdminToken = result.Admin.Token;
        BotToken = result.Bot.Token;
        BotUserId = result.Bot.UserId;
        TeamId = result.TeamId;
        ChannelId = result.ChannelId;
        TestUserId = result.TestUser.UserId;
        _testUserToken = result.TestUser.Token;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    /// <summary>
    /// Skips the calling test when no container runtime started the Mattermost
    /// server. Call this first in each test's setup so the suite degrades to
    /// skipped rather than failed on hosts without Docker.
    /// </summary>
    public void SkipIfUnavailable()
    {
        if (SkipReason is not null)
            Assert.Skip(SkipReason);
    }

    /// <summary>
    /// Recognizes the exception types Testcontainers throws when no Docker
    /// daemon is reachable. On Linux runners with Docker none of these match
    /// and the suite runs normally.
    /// </summary>
    private static bool IsDockerUnavailable(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current.GetType().Name.Contains("Docker", StringComparison.Ordinal))
                return true;

            var msg = current.Message ?? "";
            if (msg.Contains("Docker", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("named pipe", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("/var/run/docker.sock", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public HttpClient CreateHttpClient()
    {
        return new HttpClient { BaseAddress = new Uri(ServerUrl) };
    }

    public HttpClient CreateBotApiClient()
    {
        var http = CreateHttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", BotToken);
        return http;
    }

    /// <summary>
    /// Creates an authenticated HttpClient that can act as the test user.
    /// </summary>
    public async Task<(HttpClient Client, string Token)> CreateTestUserClientAsync()
    {
        var http = CreateHttpClient();
        // Re-login each call to keep parity with the prior behavior — tests
        // that hold the client across the fixture's lifetime get a fresh
        // token rather than reusing the one cached at seed time.
        var loginResponse = await http.PostAsJsonAsync("/api/v4/users/login", new
        {
            login_id = SeedOptions.TestUserUsername,
            password = SeedOptions.TestUserPassword,
        });
        loginResponse.EnsureSuccessStatusCode();
        if (!loginResponse.Headers.TryGetValues("Token", out var tokens))
            throw new InvalidOperationException("Mattermost login did not return a Token header.");
        var token = tokens.First();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (http, token);
    }

    /// <summary>
    /// Posts a message as the test user. Returns the post ID.
    /// </summary>
    public async Task<string> PostAsTestUserAsync(string channelId, string text, string? rootId = null)
    {
        using var http = CreateHttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _testUserToken);
        {
            var payload = new Dictionary<string, object?>
            {
                ["channel_id"] = channelId,
                ["message"] = text
            };
            if (!string.IsNullOrEmpty(rootId))
                payload["root_id"] = rootId;

            var response = await http.PostAsJsonAsync("/api/v4/posts", payload);
            response.EnsureSuccessStatusCode();
            var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            return doc.RootElement.GetProperty("id").GetString()!;
        }
    }
}

/// <summary>
/// Additional wait strategy that ensures the API is actually ready to accept user registration,
/// not just returning 200 on /ping.
/// </summary>
internal sealed class WaitUntilApiReady : IWaitUntil
{
    public async Task<bool> UntilAsync(IContainer container)
    {
        try
        {
            var port = container.GetMappedPublicPort(8065);
            using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };

            // The /ping endpoint returns 200 early, but the API may not be ready
            // for user creation yet. Try the users endpoint to confirm.
            var response = await http.GetAsync("/api/v4/users/me");

            // 401 means the API is up and rejecting unauthenticated requests — ready
            return response.StatusCode == HttpStatusCode.Unauthorized;
        }
        catch
        {
            return false;
        }
    }
}

[CollectionDefinition("Mattermost")]
public class MattermostCollection : ICollectionFixture<MattermostFixture>;
