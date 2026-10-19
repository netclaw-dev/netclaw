// -----------------------------------------------------------------------
// <copyright file="LifecycleEndpointRouteBuilderExtensionsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Daemon.Lifecycle;
using Netclaw.Daemon.Tests.Configuration;
using Netclaw.Daemon.Security;
using Netclaw.Daemon.Services;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Lifecycle;

/// <summary>
/// Real integration tests for the lifecycle endpoints registered by
/// <see cref="LifecycleEndpointRouteBuilderExtensions.MapLifecycleEndpoints"/>.
///
/// The test host calls the actual extension method — no handler reimplementation.
/// </summary>
/// <remarks>
/// #2527 rule 1: a shutdown notice carries the PID and the home of the daemon that the caller
/// means to stop. The daemon compares both with its own PID (<see cref="Environment.ProcessId"/>)
/// and its own home (<see cref="NetclawPaths.BasePath"/>). A mismatch gets 409 and a Warning log.
/// A missing or malformed value gets 400. Only a match logs "Netclaw daemon stopping" and sends
/// the <c>daemon.stopping</c> alert.
/// </remarks>
public sealed class LifecycleEndpointRouteBuilderExtensionsTests : IAsyncDisposable
{
    private const string StoppingLogText = "Netclaw daemon stopping";

    private readonly TrackingNotificationSink _sink = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly DisposableTempDir _dir = new();

    // The home of the daemon under test. The test host has no files in it.
    private string DaemonHome => Path.Combine(_dir.Path, "daemon-home");

    private static int OwnPid => Environment.ProcessId;

    public ValueTask DisposeAsync()
    {
        _dir.Dispose();
        return ValueTask.CompletedTask;
    }

    // ─── App factory ───────────────────────────────────────────────────────────

    private async Task<WebApplication> CreateAppAsync(bool spoofLoopback, string? servedHome = null)
    {
        Directory.CreateDirectory(DaemonHome);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddNetclawAuthSchemes(new DaemonConfig());
        builder.Services.AddAuthorization();
        builder.Services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(_logs);
        });
        builder.Services.AddSingleton<IOperationalNotificationSink>(_sink);
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton(new NetclawPaths(servedHome ?? DaemonHome));
        builder.Services.AddSingleton<DaemonLifecycleNotifier>();

        var app = builder.Build();

        if (spoofLoopback)
        {
            app.Use(async (ctx, next) =>
            {
                ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
                await next(ctx);
            });
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapLifecycleEndpoints();

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static string Notice(string? reason, string? pid, string? home)
    {
        var query = new List<string>();
        if (reason is not null)
            query.Add($"reason={Uri.EscapeDataString(reason)}");
        if (pid is not null)
            query.Add($"pid={Uri.EscapeDataString(pid)}");
        if (home is not null)
            query.Add($"home={Uri.EscapeDataString(home)}");

        return "/api/lifecycle/shutdown" + (query.Count == 0 ? string.Empty : "?" + string.Join('&', query));
    }

    private string MatchingNotice(string reason = "cli-stop")
        => Notice(reason, OwnPid.ToString(CultureInfo.InvariantCulture), DaemonHome);

    private void AssertNothingAnnounced()
    {
        Assert.Empty(_sink.Emitted);
        Assert.DoesNotContain(_logs.Entries, entry => entry.Message.Contains(StoppingLogText, StringComparison.Ordinal));
    }

    // ─── POST /api/lifecycle/shutdown ─────────────────────────────────────────

    [Fact]
    public async Task Shutdown_returns_401_for_unauthenticated_request()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: false);
        var client = app.GetTestClient();

        var response = await client.PostAsync(MatchingNotice(), null, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertNothingAnnounced();
    }

    [Fact]
    public async Task Shutdown_returns_400_when_reason_is_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.PostAsync(
            Notice(reason: null, OwnPid.ToString(CultureInfo.InvariantCulture), DaemonHome), null, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Contains("reason", body.GetProperty("error").GetString());
        AssertNothingAnnounced();
    }

    [Fact]
    public async Task Shutdown_returns_200_and_announces_the_stop_when_pid_and_home_match()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        const string shutdownReason = "cli-stop";
        var response = await client.PostAsync(MatchingNotice(shutdownReason), null, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(shutdownReason, body.GetProperty("reason").GetString());
        Assert.Equal(OwnPid, body.GetProperty("pid").GetInt32());
        // S2: the CLI accepts a 200 as proof only when it names this home as well as this PID.
        Assert.Equal(HomeIdentity.Canonical(DaemonHome), body.GetProperty("home").GetString());

        // Verify NotifyShutdown was invoked via the notification sink
        Assert.Single(_sink.Emitted);
        var alert = _sink.Emitted[0];
        Assert.Equal("daemon.stopping", alert.Type);
        Assert.NotNull(alert.Context);
        Assert.Equal(shutdownReason, alert.Context["reason"]);
        Assert.Single(_logs.Entries, entry => entry.Message.Contains(StoppingLogText, StringComparison.Ordinal));
        Assert.DoesNotContain(
            _logs.Entries,
            entry => entry.Level >= LogLevel.Warning && entry.Message.Contains(DaemonHome, StringComparison.Ordinal));
    }

    public static TheoryData<string> SpellingsOfTheDaemonHome() => new()
    {
        { "with-trailing-separator" },
        { "with-dot-segment" },
    };

    [Theory]
    [MemberData(nameof(SpellingsOfTheDaemonHome))]
    public async Task Shutdown_accepts_another_spelling_of_the_same_home(string spelling)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();
        var home = spelling switch
        {
            "with-trailing-separator" => DaemonHome + Path.DirectorySeparatorChar,
            _ => Path.Combine(DaemonHome, "sub", ".."),
        };
        Directory.CreateDirectory(Path.Combine(DaemonHome, "sub"));

        var response = await client.PostAsync(
            Notice("cli-stop", OwnPid.ToString(CultureInfo.InvariantCulture), home), null, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(_sink.Emitted);
    }

    [SlopwatchSuppress("SW001", "Creating a symbolic link needs a privilege on Windows.")]
    [Fact(SkipUnless = nameof(TestPlatform.IsPosix), SkipType = typeof(TestPlatform), Skip = "Creating a symbolic link needs a privilege on Windows.")]
    public async Task Shutdown_accepts_a_symbolic_link_to_the_home()
    {
        // The CLI sends the home with links resolved, but the daemon home can be a link (for
        // example NETCLAW_HOME set to a link). Both sides must name the same directory.
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();
        var link = Path.Combine(_dir.Path, "link-to-daemon-home");
        Directory.CreateSymbolicLink(link, DaemonHome);

        var response = await client.PostAsync(
            Notice("cli-stop", OwnPid.ToString(CultureInfo.InvariantCulture), link), null, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(_sink.Emitted);
    }

    [SlopwatchSuppress("SW001", "Creating a symbolic link needs a privilege on Windows.")]
    [Fact(SkipUnless = nameof(TestPlatform.IsPosix), SkipType = typeof(TestPlatform), Skip = "Creating a symbolic link needs a privilege on Windows.")]
    public async Task Shutdown_200_names_the_resolved_home_when_the_daemon_serves_a_link_to_its_home()
    {
        // S2: the daemon serves the home through a link. The answer names the folder behind the link, which is
        // the form that every CLI sends. A CLI that read the unresolved text would not match its own notice.
        var ct = TestContext.Current.CancellationToken;
        var link = Path.Combine(_dir.Path, "link-to-daemon-home");
        Directory.CreateDirectory(DaemonHome);
        Directory.CreateSymbolicLink(link, DaemonHome);
        await using var app = await CreateAppAsync(spoofLoopback: true, servedHome: link);
        var client = app.GetTestClient();

        var response = await client.PostAsync(
            Notice("cli-stop", OwnPid.ToString(CultureInfo.InvariantCulture), DaemonHome), null, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(HomeIdentity.Canonical(DaemonHome), body.GetProperty("home").GetString());
        Assert.NotEqual(link, body.GetProperty("home").GetString());
    }

    [SlopwatchSuppress("SW001", "Creating a symbolic link needs a privilege on Windows.")]
    [Fact(SkipUnless = nameof(TestPlatform.IsPosix), SkipType = typeof(TestPlatform), Skip = "Creating a symbolic link needs a privilege on Windows.")]
    public async Task Shutdown_returns_409_not_a_server_error_when_the_home_is_a_link_cycle()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();
        var loop = Path.Combine(_dir.Path, "loop");
        Directory.CreateSymbolicLink(loop, "loop");

        var response = await client.PostAsync(
            Notice("cli-stop", OwnPid.ToString(CultureInfo.InvariantCulture), loop), null, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertNothingAnnounced();
    }

    [SlopwatchSuppress("SW001", "Creating a symbolic link needs a privilege on Windows.")]
    [Fact(SkipUnless = nameof(TestPlatform.IsPosix), SkipType = typeof(TestPlatform), Skip = "Creating a symbolic link needs a privilege on Windows.")]
    public async Task Shutdown_returns_409_when_a_dot_dot_in_a_link_target_points_at_another_folder()
    {
        // S1 at the endpoint: with a=b/c, b=q/r, and q/r/c/link=../../y, the folder of a/link is q/y. A notice
        // whose home is that link must not match a daemon that serves the other folder y.
        var ct = TestContext.Current.CancellationToken;
        var other = Path.Combine(_dir.Path, "y");
        Directory.CreateDirectory(other);
        Directory.CreateDirectory(Path.Combine(_dir.Path, "q", "r", "c"));
        Directory.CreateDirectory(Path.Combine(_dir.Path, "q", "y"));
        Directory.CreateSymbolicLink(Path.Combine(_dir.Path, "b"), Path.Combine("q", "r"));
        Directory.CreateSymbolicLink(Path.Combine(_dir.Path, "a"), Path.Combine("b", "c"));
        Directory.CreateSymbolicLink(Path.Combine(_dir.Path, "q", "r", "c", "link"), Path.Combine("..", "..", "y"));
        await using var app = await CreateAppAsync(spoofLoopback: true, servedHome: other);
        var client = app.GetTestClient();

        var response = await client.PostAsync(
            Notice("cli-stop", OwnPid.ToString(CultureInfo.InvariantCulture), Path.Combine(_dir.Path, "a", "link")), null, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertNothingAnnounced();
    }

    public static TheoryData<string> LogInjectionCharacters() => new()
    {
        { "\n" }, { "\r" }, { "\r\n" }, { "\u2028" }, { "\u2029" }, { "\u0085" }, { "\u001b" }, { "\t" }, { "\u007f" },
    };

    [Theory]
    [MemberData(nameof(LogInjectionCharacters))]
    public async Task Shutdown_removes_control_characters_and_line_breaks_from_the_home_before_it_logs(string injected)
    {
        // S5: the home comes from the query string. A line break in it must not start a forged log line.
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();
        // The log cuts the home at 200 characters. The marker sits right after the root of the file system, so
        // the length of the temp folder (long on macOS) cannot push it past the cut.
        var home = Path.Combine(Path.GetPathRoot(_dir.Path)!, "x" + injected + "fake-log-line level=critical");

        var response = await client.PostAsync(
            Notice("cli-stop", OwnPid.ToString(CultureInfo.InvariantCulture), home), null, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var warning = Assert.Single(
            _logs.Entries,
            entry => entry.Level >= LogLevel.Warning && entry.Message.Contains("fake-log-line", StringComparison.Ordinal));
        Assert.False(
            warning.Message.Any(static character => char.IsControl(character) || character is '\u2028' or '\u2029'),
            "The Warning holds a control character or a line separator.");
        Assert.Contains(DaemonHome, warning.Message, StringComparison.Ordinal);
        AssertNothingAnnounced();
    }

    [Fact]
    public async Task Shutdown_cuts_a_very_long_home_in_the_log()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();
        // Start at the root of the file system, so the length of the temp folder cannot hide the cut.
        var home = Path.Combine(Path.GetPathRoot(_dir.Path)!, new string('a', 600));

        var response = await client.PostAsync(
            Notice("cli-stop", OwnPid.ToString(CultureInfo.InvariantCulture), home), null, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var warning = Assert.Single(
            _logs.Entries,
            entry => entry.Level >= LogLevel.Warning && entry.Message.Contains(new string('a', 20), StringComparison.Ordinal));
        Assert.DoesNotContain(new string('a', 201), warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shutdown_returns_409_and_stays_silent_when_the_pid_is_another_process()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();
        var otherPid = OwnPid + 1;

        var response = await client.PostAsync(
            Notice("cli-stop", otherPid.ToString(CultureInfo.InvariantCulture), DaemonHome), null, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("error").GetString();
        Assert.Contains(otherPid.ToString(CultureInfo.InvariantCulture), error, StringComparison.Ordinal);
        Assert.Contains(OwnPid.ToString(CultureInfo.InvariantCulture), error, StringComparison.Ordinal);
        AssertNothingAnnounced();
        AssertOneWarningNames(DaemonHome);
    }

    [Fact]
    public async Task Shutdown_returns_409_and_stays_silent_when_the_home_is_another_home()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();
        var otherHome = Path.Combine(_dir.Path, "other-home");
        Directory.CreateDirectory(otherHome);

        var response = await client.PostAsync(
            Notice("cli-stop", OwnPid.ToString(CultureInfo.InvariantCulture), otherHome), null, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("error").GetString();
        Assert.Contains(otherHome, error, StringComparison.Ordinal);
        Assert.Contains(DaemonHome, error, StringComparison.Ordinal);
        AssertNothingAnnounced();
        AssertOneWarningNames(otherHome, DaemonHome);
    }

    [Fact]
    public async Task Shutdown_returns_409_when_both_the_pid_and_the_home_are_wrong()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();
        var otherHome = Path.Combine(_dir.Path, "other-home");

        var response = await client.PostAsync(
            Notice("cli-stop", (OwnPid + 1).ToString(CultureInfo.InvariantCulture), otherHome), null, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertNothingAnnounced();
    }

    [Fact]
    public async Task Shutdown_returns_409_when_the_home_is_a_folder_inside_the_daemon_home()
    {
        // A prefix is not the same home. A comparison that tests StartsWith would accept this.
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();
        var inside = Path.Combine(DaemonHome, "workspaces");
        Directory.CreateDirectory(inside);

        var response = await client.PostAsync(
            Notice("cli-stop", OwnPid.ToString(CultureInfo.InvariantCulture), inside), null, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertNothingAnnounced();
    }

    [Fact]
    public async Task Shutdown_returns_409_when_the_home_does_not_exist()
    {
        // A path part that does not exist is not a link. The comparison keeps it as spelled and refuses
        // the notice. It must not fail with a server error.
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();
        var missing = Path.Combine(_dir.Path, "no-such-home", "sub");

        var response = await client.PostAsync(
            Notice("cli-stop", OwnPid.ToString(CultureInfo.InvariantCulture), missing), null, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertNothingAnnounced();
    }

    public static TheoryData<string?, string?, string> MalformedIdentities() => new()
    {
        { null, null, "neither pid nor home" },
        { null, "@HOME", "no pid" },
        { "@PID", null, "no home" },
        { "", "@HOME", "empty pid" },
        { "@PID", "", "empty home" },
        { "not-a-number", "@HOME", "non-numeric pid" },
        { "0", "@HOME", "zero pid" },
        { "-1", "@HOME", "negative pid" },
        { "@PID", "relative/home", "relative home" },
        { "@PID", "@HOME\0x", "home with a NUL character" },
        { "@PID", "\\x", "drive-relative home with a backslash (not fully qualified on Windows)" },
        { "@PID", "C:x", "drive-relative home (not fully qualified on Windows)" },
    };

    [Theory]
    [MemberData(nameof(MalformedIdentities))]
    public async Task Shutdown_returns_400_and_stays_silent_when_pid_or_home_is_missing_or_malformed(
        string? pid, string? home, string because)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();
        var resolvedPid = pid?.Replace("@PID", OwnPid.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var resolvedHome = home?.Replace("@HOME", DaemonHome, StringComparison.Ordinal);

        var response = await client.PostAsync(Notice("cli-stop", resolvedPid, resolvedHome), null, ct);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{because}: got {(int)response.StatusCode}");
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("error").GetString();
        Assert.Contains("pid", error, StringComparison.Ordinal);
        Assert.Contains("home", error, StringComparison.Ordinal);
        AssertNothingAnnounced();
    }

    private void AssertOneWarningNames(params string[] homes)
    {
        // The host can log unrelated warnings. The refusal is the only one that names a home of this test.
        var warning = Assert.Single(
            _logs.Entries,
            entry => entry.Level >= LogLevel.Warning && entry.Message.Contains(homes[0], StringComparison.Ordinal));
        foreach (var home in homes)
            Assert.Contains(home, warning.Message, StringComparison.Ordinal);
    }

    // ─── Test doubles ─────────────────────────────────────────────────────────

    /// <summary>
    /// Records all emitted operational alerts so tests can assert that
    /// <see cref="DaemonLifecycleNotifier.NotifyShutdown"/> was invoked.
    /// </summary>
    private sealed class TrackingNotificationSink : IOperationalNotificationSink
    {
        private readonly List<OperationalAlert> _emitted = [];
        public IReadOnlyList<OperationalAlert> Emitted => _emitted;

        public void Emit(OperationalAlert alert) => _emitted.Add(alert);
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly Lock _gate = new();
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_gate)
                    return [.. _entries];
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private void Add(LogLevel level, string message)
        {
            lock (_gate)
                _entries.Add(new LogEntry(level, message));
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => owner.Add(logLevel, formatter(state, exception));
        }
    }
}
