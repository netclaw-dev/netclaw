// -----------------------------------------------------------------------
// <copyright file="ShutdownNoticeIdentityMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Daemon.Lifecycle;
using Netclaw.Daemon.Security;
using Netclaw.Daemon.Services;
using Xunit;

namespace Netclaw.Actors.MutationTests;

/// <summary>
/// Kills mutants in the identity check of <c>POST /api/lifecycle/shutdown</c> (#2527 rule 1). The
/// daemon announces its stop, and the CLI signals a PID, only for a notice that carries the PID
/// and the home of this daemon. A mutant that accepts a notice for another daemon sends a false
/// "stopping" alert, or lets a CLI of one home stop the daemon of another home.
/// </summary>
/// <remarks>
/// The tests run the real endpoint in a test host. They are the xUnit 2 twin of the identity tests
/// in <c>Netclaw.Daemon.Tests</c>, because Stryker's adapter does not support xUnit 3.
/// </remarks>
public sealed class ShutdownNoticeIdentityMutationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netclaw-mutation-shutdown-{Guid.NewGuid():N}");
    private readonly RecordingSink _sink = new();
    private readonly RecordingLoggerProvider _logs = new();

    private string DaemonHome => Path.Combine(_root, "daemon-home");

    private static string OwnPid => Environment.ProcessId.ToString(CultureInfo.InvariantCulture);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task A_notice_for_this_pid_and_this_home_is_announced()
    {
        var (response, body) = await PostAsync(OwnPid, DaemonHome);

        Assert.Equal(HttpStatusCode.OK, response);
        Assert.Single(_sink.Alerts);
        Assert.Single(_logs.Messages, message => message.Contains("Netclaw daemon stopping", StringComparison.Ordinal));
        // S2: the answer names the resolved home as well as the PID, so a CLI can prove the home.
        using var document = JsonDocument.Parse(body);
        Assert.Equal(Environment.ProcessId, document.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal(HomeIdentity.Canonical(DaemonHome), document.RootElement.GetProperty("home").GetString());
    }

    [Fact]
    public async Task The_answer_names_the_folder_behind_a_link_when_the_daemon_serves_a_link()
    {
        var link = Path.Combine(_root, "link-to-daemon-home");
        Directory.CreateDirectory(DaemonHome);
        Directory.CreateSymbolicLink(link, DaemonHome);

        var (response, body) = await PostAsync(OwnPid, DaemonHome, servedHome: link);

        Assert.Equal(HttpStatusCode.OK, response);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(HomeIdentity.Canonical(DaemonHome), document.RootElement.GetProperty("home").GetString());
    }

    [Fact]
    public async Task A_dot_dot_in_a_link_target_that_points_at_another_folder_is_not_this_home()
    {
        // S1: a=b/c, b=q/r, q/r/c/link=../../y. The folder of a/link is q/y, so it is not the served folder y.
        var other = Path.Combine(_root, "y");
        Directory.CreateDirectory(other);
        Directory.CreateDirectory(Path.Combine(_root, "q", "r", "c"));
        Directory.CreateDirectory(Path.Combine(_root, "q", "y"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "b"), Path.Combine("q", "r"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "a"), Path.Combine("b", "c"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "q", "r", "c", "link"), Path.Combine("..", "..", "y"));

        var (response, _) = await PostAsync(OwnPid, Path.Combine(_root, "a", "link"), servedHome: other);
        var (matching, _) = await PostAsync(OwnPid, Path.Combine(_root, "a", "link"), servedHome: Path.Combine(_root, "q", "y"));

        Assert.Equal(HttpStatusCode.Conflict, response);
        Assert.Equal(HttpStatusCode.OK, matching);
    }

    [Fact]
    public async Task A_notice_for_a_home_that_is_a_link_cycle_is_refused_not_a_server_error()
    {
        var loop = Path.Combine(_root, "loop");
        Directory.CreateDirectory(_root);
        Directory.CreateSymbolicLink(loop, "loop");

        var (response, _) = await PostAsync(OwnPid, loop);

        Assert.Equal(HttpStatusCode.Conflict, response);
        Assert.Empty(_sink.Alerts);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    [InlineData("\u0085")]
    [InlineData("\u001b")]
    [InlineData("\t")]
    public async Task Control_characters_and_line_breaks_in_the_home_do_not_reach_the_log(string injected)
    {
        var (response, _) = await PostAsync(OwnPid, Path.Combine(_root, "x" + injected + "fake-log-line level=critical"));

        Assert.Equal(HttpStatusCode.Conflict, response);
        var warning = Assert.Single(_logs.Warnings, message => message.Contains("fake-log-line", StringComparison.Ordinal));
        Assert.False(
            warning.Any(static character => char.IsControl(character) || character is '\u2028' or '\u2029'),
            "The Warning holds a control character or a line separator.");
    }

    [Fact]
    public async Task A_very_long_home_is_cut_in_the_log()
    {
        var (response, _) = await PostAsync(OwnPid, Path.Combine(_root, new string('a', 600)));

        Assert.Equal(HttpStatusCode.Conflict, response);
        var warning = Assert.Single(_logs.Warnings, message => message.Contains(new string('a', 20), StringComparison.Ordinal));
        Assert.DoesNotContain(new string('a', 201), warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("trailing-separator")]
    [InlineData("dot-segment")]
    public async Task Another_spelling_of_this_home_is_announced(string spelling)
    {
        Directory.CreateDirectory(Path.Combine(DaemonHome, "sub"));
        var home = spelling == "trailing-separator"
            ? DaemonHome + Path.DirectorySeparatorChar
            : Path.Combine(DaemonHome, "sub", "..");

        var (response, _) = await PostAsync(OwnPid, home);

        Assert.Equal(HttpStatusCode.OK, response);
        Assert.Single(_sink.Alerts);
    }

    [Fact]
    public async Task A_chain_of_links_to_this_home_is_this_home()
    {
        // The endpoint resolves each side once, so a link that points to a link must resolve to the end.
        Directory.CreateDirectory(DaemonHome);
        var first = Path.Combine(_root, "first-link");
        var second = Path.Combine(_root, "second-link");
        Directory.CreateSymbolicLink(first, DaemonHome);
        Directory.CreateSymbolicLink(second, first);

        var (response, _) = await PostAsync(OwnPid, second);

        Assert.Equal(HttpStatusCode.OK, response);
        Assert.Single(_sink.Alerts);
    }

    [Fact]
    public async Task A_notice_for_another_pid_is_refused_and_logged()
    {
        var otherPid = (Environment.ProcessId + 1).ToString(CultureInfo.InvariantCulture);
        var (response, body) = await PostAsync(otherPid, DaemonHome);

        Assert.Equal(HttpStatusCode.Conflict, response);
        // The body names both daemons, so an operator can see which one answered.
        Assert.Contains(otherPid, body, StringComparison.Ordinal);
        Assert.Contains(OwnPid, body, StringComparison.Ordinal);
        AssertSilentExceptForOneWarning();
    }

    [Fact]
    public async Task A_notice_for_another_home_is_refused_and_logged()
    {
        var otherHome = Path.Combine(_root, "other-home");
        var (response, body) = await PostAsync(OwnPid, otherHome);

        Assert.Equal(HttpStatusCode.Conflict, response);
        // The body is JSON, which escapes the backslash of a Windows path. Compare the decoded text.
        using var document = JsonDocument.Parse(body);
        var error = document.RootElement.GetProperty("error").GetString()!;
        Assert.Contains(otherHome, error, StringComparison.Ordinal);
        Assert.Contains(DaemonHome, error, StringComparison.Ordinal);
        AssertSilentExceptForOneWarning();
    }

    [Fact]
    public async Task A_notice_for_a_home_that_does_not_exist_is_refused()
    {
        var (response, _) = await PostAsync(OwnPid, Path.Combine(_root, "no-such-home", "sub"));

        Assert.Equal(HttpStatusCode.Conflict, response);
    }

    [Fact]
    public async Task A_notice_for_a_folder_inside_this_home_is_refused()
    {
        var (response, _) = await PostAsync(OwnPid, Path.Combine(DaemonHome, "workspaces"));

        Assert.Equal(HttpStatusCode.Conflict, response);
        AssertSilentExceptForOneWarning();
    }

    [Theory]
    [InlineData(null, "@HOME")]
    [InlineData("@PID", null)]
    [InlineData(null, null)]
    [InlineData("", "@HOME")]
    [InlineData("@PID", "")]
    [InlineData("abc", "@HOME")]
    [InlineData("0", "@HOME")]
    [InlineData("-1", "@HOME")]
    [InlineData("@PID", "relative/home")]
    [InlineData("@PID", "@HOME\0x")]
    [InlineData("@PID", "\\x")]
    [InlineData("@PID", "C:x")]
    public async Task A_notice_with_a_missing_or_malformed_pid_or_home_is_a_bad_request(string? pid, string? home)
    {
        var (response, body) = await PostAsync(pid?.Replace("@PID", OwnPid, StringComparison.Ordinal), home?.Replace("@HOME", DaemonHome, StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.BadRequest, response);
        Assert.Contains("pid", body, StringComparison.Ordinal);
        Assert.Contains("home", body, StringComparison.Ordinal);
        Assert.Empty(_sink.Alerts);
        Assert.DoesNotContain(_logs.Messages, message => message.Contains("Netclaw daemon stopping", StringComparison.Ordinal));
    }

    private void AssertSilentExceptForOneWarning()
    {
        Assert.Empty(_sink.Alerts);
        Assert.DoesNotContain(_logs.Messages, message => message.Contains("Netclaw daemon stopping", StringComparison.Ordinal));
        Assert.Single(_logs.Warnings, message => message.Contains(DaemonHome, StringComparison.Ordinal));
    }

    private async Task<(HttpStatusCode Status, string Body)> PostAsync(string? pid, string? home, string? servedHome = null)
    {
        Directory.CreateDirectory(DaemonHome);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddNetclawAuthSchemes(new DaemonConfig());
        builder.Services.AddAuthorization();
        builder.Services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(_logs);
        });
        builder.Services.AddSingleton<IOperationalNotificationSink>(_sink);
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton(new NetclawPaths(servedHome ?? DaemonHome));
        builder.Services.AddSingleton<DaemonLifecycleNotifier>();

        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            await next(context);
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapLifecycleEndpoints();
        await app.StartAsync();

        var query = new List<string> { "reason=cli-stop" };
        if (pid is not null)
            query.Add($"pid={Uri.EscapeDataString(pid)}");
        if (home is not null)
            query.Add($"home={Uri.EscapeDataString(home)}");

        var response = await app.GetTestClient().PostAsync("/api/lifecycle/shutdown?" + string.Join('&', query), null);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private sealed class RecordingSink : IOperationalNotificationSink
    {
        public List<OperationalAlert> Alerts { get; } = [];

        public void Emit(OperationalAlert alert) => Alerts.Add(alert);
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly object _gate = new();
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_gate)
                    return [.. _entries.Select(static entry => entry.Message)];
            }
        }

        public IReadOnlyList<string> Warnings
        {
            get
            {
                lock (_gate)
                    return [.. _entries.Where(static entry => entry.Level >= LogLevel.Warning).Select(static entry => entry.Message)];
            }
        }

        public ILogger CreateLogger(string categoryName) => new Recorder(this);

        public void Dispose()
        {
        }

        private sealed class Recorder(RecordingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner._gate)
                    owner._entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
