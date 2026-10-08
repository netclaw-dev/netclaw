// -----------------------------------------------------------------------
// <copyright file="RetentionConfigPageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Config;
using Netclaw.Cli.Tui.Config;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Termina;
using Termina.Input;
using Termina.Terminal;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

public sealed class RetentionConfigPageTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public RetentionConfigPageTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        File.WriteAllText(_paths.NetclawConfigPath, """{ "configVersion": 1 }""");
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Page_shows_the_setting_and_its_default()
    {
        var (terminal, app, _) = CreateHeadlessApp(out var input);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.True(terminal.Contains("Daemon and crash logs"), $"Screen:\n{terminal}");
        Assert.True(terminal.Contains("keep 14 days (default)"), $"Screen:\n{terminal}");
    }

    [Fact]
    public async Task Typing_a_number_and_pressing_Enter_saves_it_and_says_the_daemon_applies_it()
    {
        var (terminal, app, _) = CreateHeadlessApp(out var input);
        input.EnqueueString("30");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(30, RetentionConfigStore.Read(_paths, RetentionSettings.Logs).Days);
        Assert.True(terminal.Contains("keep 30 days"), $"Screen:\n{terminal}");
        Assert.False(terminal.Contains("(not saved)"), $"The row must show the saved value. Screen:\n{terminal}");
        Assert.True(terminal.Contains("A running daemon applies the change automatically."), $"Screen:\n{terminal}");
    }

    [Fact]
    public async Task Zero_shows_as_keep_forever()
    {
        var (terminal, app, _) = CreateHeadlessApp(out var input);
        input.EnqueueString("0");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.True(terminal.Contains("keep forever"), $"Screen:\n{terminal}");
    }

    private (VirtualTerminal Terminal, TerminaApplication App, RetentionConfigViewModel Vm)
        CreateHeadlessApp(out VirtualInputSource input)
        => HeadlessTerminaFixture.Create<RetentionConfigPage, RetentionConfigViewModel>(
            "/retention",
            _ => new RetentionConfigPage(),
            () => new RetentionConfigViewModel(_paths),
            out input);
}
