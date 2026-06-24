// -----------------------------------------------------------------------
// <copyright file="SystemdUserServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Daemon;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

public sealed class SystemdUserServiceTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();

    [Fact]
    public async Task GetOwnershipAsync_ReturnsUnmanaged_WhenUnitFileMissing()
    {
        var runner = new FakeSystemCommandRunner();
        var service = new SystemdUserService(
            Path.Combine(_dir.Path, "missing.service"),
            runner,
            enabledOnThisPlatform: true);

        var ownership = await service.GetOwnershipAsync();

        Assert.Equal(SystemdUserServiceOwnershipKind.Unmanaged, ownership.Kind);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task GetOwnershipAsync_ReturnsManaged_WhenUnitIsActive()
    {
        var unitPath = WriteUnit();
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(new SystemCommandResult(0, string.Empty));
        var service = new SystemdUserService(unitPath, runner, enabledOnThisPlatform: true);

        var ownership = await service.GetOwnershipAsync();

        Assert.Equal(SystemdUserServiceOwnershipKind.Managed, ownership.Kind);
        Assert.Equal([("systemctl", "--user is-active --quiet netclaw.service")], runner.Commands);
    }

    [Fact]
    public async Task GetOwnershipAsync_ReturnsManaged_WhenUnitIsEnabledButInactive()
    {
        var unitPath = WriteUnit();
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(new SystemCommandResult(3, string.Empty));
        runner.Enqueue(new SystemCommandResult(0, string.Empty));
        var service = new SystemdUserService(unitPath, runner, enabledOnThisPlatform: true);

        var ownership = await service.GetOwnershipAsync();

        Assert.Equal(SystemdUserServiceOwnershipKind.Managed, ownership.Kind);
        Assert.Equal(
            [
                ("systemctl", "--user is-active --quiet netclaw.service"),
                ("systemctl", "--user is-enabled --quiet netclaw.service")
            ],
            runner.Commands);
    }

    [Fact]
    public async Task GetOwnershipAsync_ReturnsUnknown_WhenSystemctlStateCheckErrors()
    {
        var unitPath = WriteUnit();
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(new SystemCommandResult(1, "Failed to connect to bus"));
        runner.Enqueue(new SystemCommandResult(1, string.Empty));
        var service = new SystemdUserService(unitPath, runner, enabledOnThisPlatform: true);

        var ownership = await service.GetOwnershipAsync();

        Assert.Equal(SystemdUserServiceOwnershipKind.Unknown, ownership.Kind);
        Assert.Contains("Could not determine", ownership.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAndStop_RunSystemctlUserCommands()
    {
        var unitPath = WriteUnit();
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(new SystemCommandResult(0, string.Empty));
        runner.Enqueue(new SystemCommandResult(0, string.Empty));
        var service = new SystemdUserService(unitPath, runner, enabledOnThisPlatform: true);

        var stop = await service.StopAsync();
        var start = await service.StartAsync();

        Assert.True(stop.Success);
        Assert.True(start.Success);
        Assert.Equal(
            [
                ("systemctl", "--user stop netclaw.service"),
                ("systemctl", "--user start netclaw.service")
            ],
            runner.Commands);
    }

    private string WriteUnit()
    {
        var unitPath = Path.Combine(_dir.Path, "netclaw.service");
        File.WriteAllText(unitPath, "[Service]\nExecStart=/opt/netclaw/netclawd\n");
        return unitPath;
    }

    public void Dispose() => _dir.Dispose();

    private sealed class FakeSystemCommandRunner : ISystemCommandRunner
    {
        private readonly Queue<SystemCommandResult> _results = [];

        public List<(string Command, string Arguments)> Commands { get; } = [];

        public void Enqueue(SystemCommandResult result) => _results.Enqueue(result);

        public Task<SystemCommandResult> RunAsync(string command, string arguments)
        {
            Commands.Add((command, arguments));
            return Task.FromResult(_results.Count == 0
                ? new SystemCommandResult(1, string.Empty)
                : _results.Dequeue());
        }
    }
}
