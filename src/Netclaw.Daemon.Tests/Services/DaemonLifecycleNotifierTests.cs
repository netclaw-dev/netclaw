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

    private sealed class RecordingSink : IOperationalNotificationSink
    {
        public List<OperationalAlert> Alerts { get; } = [];

        public void Emit(OperationalAlert alert) => Alerts.Add(alert);
    }
}
