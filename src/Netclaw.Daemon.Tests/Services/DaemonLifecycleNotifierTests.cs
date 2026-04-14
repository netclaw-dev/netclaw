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
        Assert.Equal("info", alert.Severity);
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
        Assert.Equal("info", alert.Severity);
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
        Assert.Equal("critical", alert.Severity);
        Assert.NotNull(alert.Context);
        Assert.Equal("daemon-unhandled", alert.Context["reason"]);
        Assert.Equal("/tmp/crash-20260414-182900.log", alert.Context["crashLogPath"]);
        Assert.Equal("C123/171313.123", alert.Context["latest_session_id"]);
    }

    private sealed class RecordingSink : IOperationalNotificationSink
    {
        public List<OperationalAlert> Alerts { get; } = [];

        public void Emit(OperationalAlert alert) => Alerts.Add(alert);
    }
}
