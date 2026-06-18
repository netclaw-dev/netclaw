// -----------------------------------------------------------------------
// <copyright file="ConfigAutosaveTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui.Config;
using R3;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

/// <summary>
/// Coverage for the shared persistence-exception wrapper used by config leaf
/// editors. When a save callback throws, the wrapper must report failure rather
/// than letting the exception escape into the Termina render loop.
/// </summary>
public sealed class ConfigAutosaveTests
{
    [Fact]
    public void Run_when_save_throws_returns_false_sets_error_status_and_redraws()
    {
        var status = new ReactiveProperty<ConfigStatusMessage>(
            new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral));
        var redraws = 0;

        var result = ConfigAutosave.Run(
            save: () => throw new IOException("disk full"),
            status,
            failurePrefix: "Channel save failed",
            requestRedraw: () => redraws++);

        Assert.False(result);
        Assert.Equal(ConfigStatusTone.Error, status.Value.Tone);
        Assert.StartsWith("Channel save failed", status.Value.Text, StringComparison.Ordinal);
        Assert.Contains("disk full", status.Value.Text, StringComparison.Ordinal);
        Assert.Equal(1, redraws);
    }

    [Fact]
    public async Task RunAsync_when_save_throws_returns_false_sets_error_status_and_redraws()
    {
        var status = new ReactiveProperty<ConfigStatusMessage>(
            new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral));
        var redraws = 0;

        var result = await ConfigAutosave.RunAsync(
            saveAsync: _ => throw new IOException("disk full"),
            status,
            failurePrefix: "Channel save failed",
            requestRedraw: () => redraws++,
            ct: TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.Equal(ConfigStatusTone.Error, status.Value.Tone);
        Assert.StartsWith("Channel save failed", status.Value.Text, StringComparison.Ordinal);
        Assert.Equal(1, redraws);
    }
}
