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

[Collection(Netclaw.Cli.Tests.RetentionEnvironmentCollection.Name)]
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

    [Fact]
    public async Task Keys_that_are_not_digits_are_ignored()
    {
        var (terminal, app, _) = CreateHeadlessApp(out var input);
        input.EnqueueString("3a-0");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(30, RetentionConfigStore.Read(_paths, RetentionSettings.Logs).Days);
        Assert.False(terminal.Contains("not a valid number"), $"Screen:\n{terminal}");
    }

    private async Task<string> Screen(string stored, Action<VirtualInputSource> keys)
    {
        File.WriteAllText(_paths.NetclawConfigPath, $$"""{ "configVersion": 1, "Retention": { "Logs": { "Days": {{stored}} } } }""");
        var (terminal, app, _) = CreateHeadlessApp(out var input);
        keys(input);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);
        return terminal.ToString();
    }

    [Fact]
    public async Task The_first_key_repaints_the_row()
    {
        var screen = await Screen("7", input => input.EnqueueString("9"));

        Assert.Contains("keep 9 days (not saved)", screen, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_first_Backspace_repaints_the_row()
    {
        var screen = await Screen("17", input => input.EnqueueKey(ConsoleKey.Backspace));

        Assert.Contains("keep 1 day (not saved)", screen, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_first_paste_repaints_the_row()
    {
        var screen = await Screen("7", input => input.EnqueuePaste("45"));

        Assert.Contains("keep 45 days (not saved)", screen, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Typing_the_saved_digit_repaints_the_row()
    {
        var screen = await Screen("7", input => input.EnqueueString("7"));

        Assert.Contains("keep 7 days (not saved)", screen, StringComparison.Ordinal);
    }

    private (VirtualTerminal Terminal, TerminaApplication App, RetentionConfigViewModel Vm)
        CreateHeadlessApp(out VirtualInputSource input)
        => HeadlessTerminaFixture.Create<RetentionConfigPage, RetentionConfigViewModel>(
            "/retention",
            _ => new RetentionConfigPage(),
            () => new RetentionConfigViewModel(_paths),
            out input);
}
