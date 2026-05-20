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
using Xunit;

namespace Netclaw.Channels.Mattermost.IntegrationTests;

/// <summary>
/// Manages a real Mattermost server container for integration testing.
/// Creates admin user, bot account with token, test team, channel, and test user.
/// </summary>
public sealed class MattermostFixture : IAsyncLifetime
{
    private const string AdminEmail = "admin@test.local";
    private const string AdminUsername = "admin";
    private const string AdminPassword = "Admin1234!";
    private const string BotUsername = "testbot";
    private const string TestUserEmail = "testuser@test.local";
    private const string TestUserUsername = "testuser";
    private const string TestUserPassword = "TestUser1234!";
    private const string TeamName = "test-team";
    private const string ChannelName = "test-channel";

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
            container = new ContainerBuilder()
                .WithImage("mattermost/mattermost-preview")
                .WithPortBinding(8065, true)
                .WithEnvironment("MM_SERVICESETTINGS_ENABLEOPENSERVER", "true")
                .WithEnvironment("MM_SERVICESETTINGS_ENABLEBOTACCOUNTCREATION", "true")
                .WithEnvironment("MM_SERVICESETTINGS_ENABLEUSERACCESSTOKENS", "true")
                .WithEnvironment("MM_TEAMSETTINGS_ENABLEOPENSERVER", "true")
                .WithEnvironment("MM_SERVICESETTINGS_ENABLETESTING", "true")
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

        using var http = CreateHttpClient();

        // Create admin user (first user gets admin privileges)
        var adminUserId = await CreateUserAsync(http, AdminEmail, AdminUsername, AdminPassword);

        // Login as admin
        AdminToken = await LoginAsync(http, AdminUsername, AdminPassword);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);

        // Create team
        TeamId = await CreateTeamAsync(http, TeamName);

        // Create bot
        (BotUserId, BotToken) = await CreateBotAsync(http, BotUsername);

        // Add bot to team
        await AddUserToTeamAsync(http, TeamId, BotUserId);

        // Create test channel
        ChannelId = await CreateChannelAsync(http, TeamId, ChannelName);

        // Add bot to channel
        await AddUserToChannelAsync(http, ChannelId, BotUserId);

        // Create test user and cache their auth token
        TestUserId = await CreateUserAsync(http, TestUserEmail, TestUserUsername, TestUserPassword);
        await AddUserToTeamAsync(http, TeamId, TestUserId);
        await AddUserToChannelAsync(http, ChannelId, TestUserId);
        _testUserToken = await LoginAsync(http, TestUserUsername, TestUserPassword);
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
        var token = await LoginAsync(http, TestUserUsername, TestUserPassword);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (http, token);
    }

    private static async Task<string> CreateUserAsync(HttpClient http, string email, string username, string password)
    {
        var response = await http.PostAsJsonAsync("/api/v4/users", new
        {
            email,
            username,
            password
        });
        response.EnsureSuccessStatusCode();
        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string> LoginAsync(HttpClient http, string loginId, string password)
    {
        var response = await http.PostAsJsonAsync("/api/v4/users/login", new
        {
            login_id = loginId,
            password
        });
        response.EnsureSuccessStatusCode();

        // Token is returned in the response header
        if (response.Headers.TryGetValues("Token", out var tokens))
            return tokens.First();

        throw new InvalidOperationException("Mattermost login did not return a Token header.");
    }

    private static async Task<string> CreateTeamAsync(HttpClient http, string name)
    {
        var response = await http.PostAsJsonAsync("/api/v4/teams", new
        {
            name,
            display_name = name,
            type = "O" // Open team
        });
        response.EnsureSuccessStatusCode();
        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<(string BotUserId, string Token)> CreateBotAsync(HttpClient http, string username)
    {
        // Create bot
        var botResponse = await http.PostAsJsonAsync("/api/v4/bots", new
        {
            username,
            display_name = "Test Bot"
        });
        botResponse.EnsureSuccessStatusCode();
        var botDoc = await JsonDocument.ParseAsync(await botResponse.Content.ReadAsStreamAsync());
        var botUserId = botDoc.RootElement.GetProperty("user_id").GetString()!;

        // Create personal access token for bot
        var tokenResponse = await http.PostAsJsonAsync($"/api/v4/users/{botUserId}/tokens", new
        {
            description = "integration-test-token"
        });
        tokenResponse.EnsureSuccessStatusCode();
        var tokenDoc = await JsonDocument.ParseAsync(await tokenResponse.Content.ReadAsStreamAsync());
        var token = tokenDoc.RootElement.GetProperty("token").GetString()!;

        return (botUserId, token);
    }

    private static async Task<string> CreateChannelAsync(HttpClient http, string teamId, string name)
    {
        var response = await http.PostAsJsonAsync("/api/v4/channels", new
        {
            team_id = teamId,
            name,
            display_name = name,
            type = "O" // Public channel
        });
        response.EnsureSuccessStatusCode();
        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task AddUserToTeamAsync(HttpClient http, string teamId, string userId)
    {
        var response = await http.PostAsJsonAsync($"/api/v4/teams/{teamId}/members", new
        {
            team_id = teamId,
            user_id = userId
        });
        response.EnsureSuccessStatusCode();
    }

    private static async Task AddUserToChannelAsync(HttpClient http, string channelId, string userId)
    {
        var response = await http.PostAsJsonAsync($"/api/v4/channels/{channelId}/members", new
        {
            user_id = userId
        });
        response.EnsureSuccessStatusCode();
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
