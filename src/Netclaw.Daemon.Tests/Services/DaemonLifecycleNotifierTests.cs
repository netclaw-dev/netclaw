// -----------------------------------------------------------------------
// <copyright file="DaemonLifecycleNotifierTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class DaemonLifecycleNotifierTests
{
    private readonly RecordingSink _sink = new();
    private readonly DaemonLifecycleNotifier _sut;

    public DaemonLifecycleNotifierTests()
    {
        _sut = new DaemonLifecycleNotifier(
            _sink,
            TimeProvider.System,
            NullLogger<DaemonLifecycleNotifier>.Instance);
    }

    [Fact]
    public void NotifyStarted_EmitsDaemonStartedAlert()
    {
        _sut.NotifyStarted();

        var alert = Assert.Single(_sink.Alerts);
        Assert.Equal("daemon.started", alert.Type);
        Assert.Equal(AlertType.DaemonStarted, alert.Category);
        Assert.Equal(AlertSeverity.Info, alert.Severity);
        Assert.NotNull(alert.Context);
        Assert.True(alert.Context.ContainsKey("pid"));
        Assert.Equal(Environment.ProcessId.ToString(), alert.Context["pid"]);
    }

    [Fact]
    public void NotifyShutdown_EmitsDaemonStoppingAlert()
    {
        _sut.NotifyShutdown("cli-stop");

        var alert = Assert.Single(_sink.Alerts);
        Assert.Equal("daemon.stopping", alert.Type);
        Assert.Equal(AlertType.DaemonStopping, alert.Category);
        Assert.Equal(AlertSeverity.Info, alert.Severity);
        Assert.NotNull(alert.Context);
        Assert.Equal("cli-stop", alert.Context["reason"]);
        Assert.Equal(Environment.ProcessId.ToString(), alert.Context["pid"]);
        Assert.Contains("cli-stop", alert.Summary);
    }

    [Fact]
    public void NotifyShutdown_IncludesReasonInSummary()
    {
        _sut.NotifyShutdown("update");

        var alert = Assert.Single(_sink.Alerts);
        Assert.Equal("Netclaw daemon stopping: update", alert.Summary);
    }

    [Fact]
    public void NotifyCrashing_EmitsCriticalAlert()
    {
        var ex = new InvalidOperationException("kaboom");

        _sut.NotifyCrashing(
            "daemon-unhandled",
            ex,
            "/tmp/crash-20260414-182900.log",
            new Dictionary<string, string> { ["latest_session_id"] = "C123/171313.123" });

        var alert = Assert.Single(_sink.Alerts);
        Assert.Equal("daemon.crashing", alert.Type);
        Assert.Equal(AlertType.DaemonCrashed, alert.Category);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.NotNull(alert.Context);
        Assert.Equal("daemon-unhandled", alert.Context["reason"]);
        Assert.Equal("/tmp/crash-20260414-182900.log", alert.Context["crashLogPath"]);
        Assert.Equal("C123/171313.123", alert.Context["latest_session_id"]);
    }

    [Theory]
    [InlineData("ok\r\nlevel=critical attacker=admin", "oklevel=critical attacker=admin")]
    [InlineData("ok\nfake-line", "okfake-line")]
    [InlineData("ok\u2028fake-line", "okfake-line")]   // U+2028 LINE SEPARATOR
    [InlineData("ok\u2029fake-line", "okfake-line")]   // U+2029 PARAGRAPH SEPARATOR
    [InlineData("ok\0NUL", "okNUL")]
    public void NotifyShutdown_strips_log_line_breakers_from_reason(string raw, string expected)
    {
        _sut.NotifyShutdown(raw);

        var alert = Assert.Single(_sink.Alerts);
        Assert.Equal(expected, alert.Context!["reason"]);
        Assert.Equal($"Netclaw daemon stopping: {expected}", alert.Summary);
    }

    [Fact]
    public void NotifyShutdown_truncates_reason_at_200_chars_without_splitting_surrogate_pair()
    {
        // 99 emoji = 198 UTF-16 chars (2 each), then 1 emoji at positions
        // 198–199, then plain ASCII tail. Position 200 would split the 100th
        // emoji's surrogate pair if we naively cut at char index 200.
        var emoji = "😀"; // U+1F600 GRINNING FACE
        var raw = string.Concat(Enumerable.Repeat(emoji, 100)) + "TAIL";

        _sut.NotifyShutdown(raw);

        var reason = Assert.Single(_sink.Alerts).Context!["reason"];
        Assert.True(reason.Length <= 200);
        Assert.False(char.IsHighSurrogate(reason[^1]),
            "truncated reason ended on a high surrogate — dangling surrogate would break UTF-8 encoders downstream");
    }

    private sealed class RecordingSink : IOperationalNotificationSink
    {
        public List<OperationalAlert> Alerts { get; } = [];

        public void Emit(OperationalAlert alert) => Alerts.Add(alert);
    }
}
