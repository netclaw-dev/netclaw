// -----------------------------------------------------------------------
// <copyright file="ShutdownNoticeIdentityMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Net;
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
        var (response, _) = await PostAsync(OwnPid, DaemonHome);

        Assert.Equal(HttpStatusCode.OK, response);
        Assert.Single(_sink.Alerts);
        Assert.Single(_logs.Messages, message => message.Contains("Netclaw daemon stopping", StringComparison.Ordinal));
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
        Assert.Contains(otherHome, body, StringComparison.Ordinal);
        Assert.Contains(DaemonHome, body, StringComparison.Ordinal);
        AssertSilentExceptForOneWarning();
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

    private async Task<(HttpStatusCode Status, string Body)> PostAsync(string? pid, string? home)
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
        builder.Services.AddSingleton(new NetclawPaths(DaemonHome));
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
