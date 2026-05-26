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
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
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
            // mattermost-preview is amd64-only; ARM hosts need an explicit
            // platform so Docker pulls the emulated image instead of failing
            // manifest resolution.
            var builder = new ContainerBuilder(
                new DockerImage("mattermost/mattermost-preview:latest", new Platform("linux/amd64")))
                .WithPortBinding(8065, true);

            foreach (var (name, value) in MattermostBootstrapper.DefaultEnvironmentVariables)
                builder = builder.WithEnvironment(name, value);

            container = builder
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(r => r
                        .ForPort(8065)
                        .ForPath("/api/v4/system/ping")
                        .ForStatusCode(HttpStatusCode.OK)))
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
    /// Creates an authenticated HttpClient that can act as the test
    /// user. Reuses the access token captured at seed time —
    /// Mattermost personal-session tokens are reusable, so there's no
    /// reason to spend a round-trip re-logging in on every call.
    /// </summary>
    public Task<(HttpClient Client, string Token)> CreateTestUserClientAsync()
    {
        var http = CreateHttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _testUserToken!);
        return Task.FromResult((http, _testUserToken!));
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

[CollectionDefinition("Mattermost")]
public class MattermostCollection : ICollectionFixture<MattermostFixture>;
