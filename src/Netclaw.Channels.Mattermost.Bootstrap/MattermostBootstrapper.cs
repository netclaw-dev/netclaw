// -----------------------------------------------------------------------
// <copyright file="MattermostBootstrapper.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Netclaw.Channels.Mattermost.Bootstrap;

/// <summary>
/// Seeds a freshly-started Mattermost server with the minimum surface a
/// NetClaw-Mattermost integration needs: an admin user, a team, a bot
/// user with a personal access token, a public channel the bot is a
/// member of, and a non-admin test user added to the same channel.
///
/// Shared between the integration test fixture
/// (<c>Netclaw.Channels.Mattermost.IntegrationTests.MattermostFixture</c>)
/// and the Aspire demo AppHost
/// (<c>samples/Netclaw.Demo.AppHost/Program.cs</c>) so the seeding
/// sequence lives in exactly one place.
/// </summary>
public static class MattermostBootstrapper
{
    /// <summary>
    /// Polls the Mattermost server until it answers <c>/api/v4/users/me</c>
    /// with HTTP 401, which proves the API is past warm-up and ready to
    /// accept user creation. Returns successfully when ready; throws
    /// <see cref="TimeoutException"/> if the deadline elapses first.
    /// </summary>
    public static async Task WaitForReadyAsync(
        Uri serverUrl,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverUrl);

        using var http = new HttpClient { BaseAddress = serverUrl };
        var deadline = DateTimeOffset.UtcNow + timeout;
        var lastStatus = "no response yet";

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // /ping returns 200 early while the DB is still migrating.
                // /users/me hitting Unauthorized is the real "API up" signal.
                var response = await http.GetAsync("/api/v4/users/me", cancellationToken);
                lastStatus = $"HTTP {(int)response.StatusCode}";

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    return;
            }
            catch (HttpRequestException ex)
            {
                lastStatus = $"{ex.GetType().Name}: {ex.Message}";
            }

            // Polling against a real external server is the documented
            // exception to CLAUDE.md's no-Task.Delay rule (the rule
            // forbids it in test orchestration; Mattermost startup has
            // no client-observable signal short of polling).
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException(
            $"Mattermost at {serverUrl} did not become ready within {timeout}. Last probe: {lastStatus}.");
    }

    /// <summary>
    /// Drives the full seeding sequence against a Mattermost server that
    /// has already been started (and has bot accounts + personal access
    /// tokens enabled — see <see cref="DefaultEnvironmentVariables"/>).
    /// Internally waits for readiness first so callers don't have to.
    /// </summary>
    public static async Task<BootstrapResult> SeedAsync(
        Uri serverUrl,
        BootstrapOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverUrl);
        ArgumentNullException.ThrowIfNull(options);

        await WaitForReadyAsync(serverUrl, options.ReadinessTimeout, cancellationToken);

        using var http = new HttpClient { BaseAddress = serverUrl };

        // First user is auto-promoted to system admin by Mattermost.
        var adminUserId = await CreateUserAsync(
            http, options.AdminEmail, options.AdminUsername, options.AdminPassword, cancellationToken);

        var adminToken = await LoginAsync(http, options.AdminUsername, options.AdminPassword, cancellationToken);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var teamId = await CreateTeamAsync(http, options.TeamName, cancellationToken);

        var (botUserId, botToken) = await CreateBotAsync(
            http, options.BotUsername, options.BotDisplayName, options.BotTokenDescription, cancellationToken);
        await AddUserToTeamAsync(http, teamId, botUserId, cancellationToken);

        var channelId = await CreateChannelAsync(http, teamId, options.ChannelName, cancellationToken);
        await AddUserToChannelAsync(http, channelId, botUserId, cancellationToken);

        var testUserId = await CreateUserAsync(
            http, options.TestUserEmail, options.TestUserUsername, options.TestUserPassword, cancellationToken);
        await AddUserToTeamAsync(http, teamId, testUserId, cancellationToken);
        await AddUserToChannelAsync(http, channelId, testUserId, cancellationToken);
        var testUserToken = await LoginAsync(http, options.TestUserUsername, options.TestUserPassword, cancellationToken);

        // Strip the trailing slash so consumers building URLs like
        // $"{ServerUrl}/api/..." don't end up with `//api/...` — a
        // Mattermost.NET quirk that bit the demo AppHost on its first
        // env-var wiring.
        var normalizedServerUrl = new Uri(serverUrl.ToString().TrimEnd('/'));

        return new BootstrapResult(
            ServerUrl: normalizedServerUrl,
            // Admin password is intentionally not republished on the
            // result — callers that need to re-authenticate as admin
            // already have it from the options they passed in, and
            // shrinking the secret footprint of the returned record
            // keeps tokens out of long-lived AppHost heap dumps.
            Admin: new BootstrapCredentials(adminUserId, options.AdminUsername, Password: null, adminToken),
            // Bots don't have passwords; null is the truthful value.
            Bot: new BootstrapCredentials(botUserId, options.BotUsername, Password: null, botToken),
            TestUser: new BootstrapCredentials(testUserId, options.TestUserUsername, options.TestUserPassword, testUserToken),
            TeamId: teamId,
            ChannelId: channelId);
    }

    /// <summary>
    /// Environment variables that must be set on the Mattermost container
    /// for <see cref="SeedAsync"/> to succeed. The demo AppHost and the
    /// integration test fixture both wire these on the container so the
    /// shared list lives in exactly one place.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DefaultEnvironmentVariables { get; } =
        new Dictionary<string, string>
        {
            ["MM_SERVICESETTINGS_ENABLEOPENSERVER"] = "true",
            ["MM_SERVICESETTINGS_ENABLEBOTACCOUNTCREATION"] = "true",
            ["MM_SERVICESETTINGS_ENABLEUSERACCESSTOKENS"] = "true",
            ["MM_TEAMSETTINGS_ENABLEOPENSERVER"] = "true",
            ["MM_SERVICESETTINGS_ENABLETESTING"] = "true",
        };

    private static async Task<string> CreateUserAsync(
        HttpClient http, string email, string username, string password, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync(
            "/api/v4/users",
            new { email, username, password },
            ct);
        await EnsureSuccessAsync(response, $"create user {username}", ct);
        return await ReadStringPropertyAsync(response, "id", ct);
    }

    /// <summary>
    /// Authenticates against a Mattermost server and returns the bearer
    /// token from the <c>Token</c> response header. Public so the
    /// integration test fixture and the Aspire smoke test can reuse it
    /// instead of re-implementing the login REST call.
    /// </summary>
    public static async Task<string> LoginAsync(
        HttpClient http, string loginId, string password, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync(
            "/api/v4/users/login",
            new { login_id = loginId, password },
            ct);
        await EnsureSuccessAsync(response, $"login as {loginId}", ct);

        if (response.Headers.TryGetValues("Token", out var tokens))
            return tokens.First();

        throw new InvalidOperationException(
            $"Mattermost login for {loginId} did not return a Token header.");
    }

    private static async Task<string> CreateTeamAsync(HttpClient http, string name, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync(
            "/api/v4/teams",
            new { name, display_name = name, type = "O" },
            ct);
        await EnsureSuccessAsync(response, $"create team {name}", ct);
        return await ReadStringPropertyAsync(response, "id", ct);
    }

    private static async Task<(string BotUserId, string Token)> CreateBotAsync(
        HttpClient http, string username, string displayName, string tokenDescription, CancellationToken ct)
    {
        var botResponse = await http.PostAsJsonAsync(
            "/api/v4/bots",
            new { username, display_name = displayName },
            ct);
        await EnsureSuccessAsync(botResponse, $"create bot {username}", ct);
        var botUserId = await ReadStringPropertyAsync(botResponse, "user_id", ct);

        var tokenResponse = await http.PostAsJsonAsync(
            $"/api/v4/users/{botUserId}/tokens",
            new { description = tokenDescription },
            ct);
        await EnsureSuccessAsync(tokenResponse, $"create access token for bot {username}", ct);
        var token = await ReadStringPropertyAsync(tokenResponse, "token", ct);

        return (botUserId, token);
    }

    private static async Task<string> CreateChannelAsync(
        HttpClient http, string teamId, string name, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync(
            "/api/v4/channels",
            new { team_id = teamId, name, display_name = name, type = "O" },
            ct);
        await EnsureSuccessAsync(response, $"create channel {name}", ct);
        return await ReadStringPropertyAsync(response, "id", ct);
    }

    private static async Task AddUserToTeamAsync(
        HttpClient http, string teamId, string userId, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync(
            $"/api/v4/teams/{teamId}/members",
            new { team_id = teamId, user_id = userId },
            ct);
        await EnsureSuccessAsync(response, $"add user {userId} to team {teamId}", ct);
    }

    private static async Task AddUserToChannelAsync(
        HttpClient http, string channelId, string userId, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync(
            $"/api/v4/channels/{channelId}/members",
            new { user_id = userId },
            ct);
        await EnsureSuccessAsync(response, $"add user {userId} to channel {channelId}", ct);
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string action, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(
            $"Mattermost API call failed: {action} returned HTTP {(int)response.StatusCode}. Body: {body}");
    }

    private static async Task<string> ReadStringPropertyAsync(
        HttpResponseMessage response, string propertyName, CancellationToken ct)
    {
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.GetProperty(propertyName).GetString()
            ?? throw new InvalidOperationException(
                $"Mattermost response missing required string property '{propertyName}'.");
    }
}

/// <summary>
/// Inputs for <see cref="MattermostBootstrapper.SeedAsync"/>. The defaults
/// produce the same seeded identities the integration test fixture has
/// always used, so callers can simply <c>new BootstrapOptions()</c>.
/// </summary>
public sealed record BootstrapOptions
{
    public string AdminEmail { get; init; } = "admin@test.local";

    public string AdminUsername { get; init; } = "admin";

    public string AdminPassword { get; init; } = "Admin1234!";

    public string BotUsername { get; init; } = "testbot";

    public string BotDisplayName { get; init; } = "Test Bot";

    public string BotTokenDescription { get; init; } = "netclaw-bootstrap-token";

    public string TestUserEmail { get; init; } = "testuser@test.local";

    public string TestUserUsername { get; init; } = "testuser";

    public string TestUserPassword { get; init; } = "TestUser1234!";

    public string TeamName { get; init; } = "test-team";

    public string ChannelName { get; init; } = "test-channel";

    public TimeSpan ReadinessTimeout { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Output of a successful <see cref="MattermostBootstrapper.SeedAsync"/>
/// call. Every field the demo or the integration test needs to drive the
/// freshly-seeded Mattermost instance ends up here.
/// </summary>
public sealed record BootstrapResult(
    Uri ServerUrl,
    BootstrapCredentials Admin,
    BootstrapCredentials Bot,
    BootstrapCredentials TestUser,
    string TeamId,
    string ChannelId);

/// <summary>
/// Identity + auth material for a single Mattermost principal.
/// <see cref="Password"/> is <c>null</c> for bot principals (they
/// only have access tokens) and for the admin principal on the
/// returned <see cref="BootstrapResult"/> (callers already pass the
/// admin password in via <see cref="BootstrapOptions.AdminPassword"/>;
/// not republishing it keeps the secret footprint of the result
/// small).
/// </summary>
public sealed record BootstrapCredentials(
    string UserId,
    string Username,
    string? Password,
    string Token);
