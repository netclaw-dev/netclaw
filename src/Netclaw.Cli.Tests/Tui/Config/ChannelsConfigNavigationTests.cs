// -----------------------------------------------------------------------
// <copyright file="ChannelsConfigNavigationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Config;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Termina;
using Termina.Hosting;
using Termina.Input;
using Termina.Terminal;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

public sealed class ChannelsConfigNavigationTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public ChannelsConfigNavigationTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Slack": { "Enabled": true, "AllowedChannelIds": ["C01"] }
            }
            """);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Channels_Escape_ReturnsToDashboardUsingTerminaHistory()
    {
        var app = CreateHeadlessApp(out var input, out var dashboardVm, out var getChannelsVm);
        dashboardVm.SelectedIndex.Value = dashboardVm.Items
            .Select((item, index) => (item, index))
            .Single(entry => entry.item.Label == "Channels")
            .index;

        dashboardVm.ActivateSelected();
        input.EnqueueKey(ConsoleKey.Escape);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.NotNull(getChannelsVm());
        Assert.Equal("/config", app.CurrentPath);
    }

    private TerminaApplication CreateHeadlessApp(
        out VirtualInputSource input,
        out ConfigDashboardViewModel dashboardVm,
        out Func<ChannelsConfigViewModel?> getChannelsVm)
    {
        var terminal = new VirtualTerminal(120, 40);
        var virtualInput = new VirtualInputSource();
        input = virtualInput;

        var navigationState = new ConfigDashboardNavigationState();
        var tuiNavigation = new TuiNavigation();
        ConfigDashboardViewModel? capturedDashboardVm = null;
        ChannelsConfigViewModel? capturedChannelsVm = null;

        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminal);
        services.AddSingleton(tuiNavigation);
        services.AddTerminaVirtualInput(virtualInput);
        services.AddTermina("/config", builder =>
        {
            builder.RegisterRoute<ConfigDashboardPage, ConfigDashboardViewModel>(
                "/config",
                _ => new ConfigDashboardPage(),
                _ =>
                {
                    capturedDashboardVm = new ConfigDashboardViewModel(navigationState);
                    return capturedDashboardVm;
                });
            builder.RegisterRoute<ChannelsConfigPage, ChannelsConfigViewModel>(
                "/channels",
                _ => new ChannelsConfigPage(),
                _ =>
                {
                    capturedChannelsVm = new ChannelsConfigViewModel(_paths, tuiNavigation);
                    return capturedChannelsVm;
                });
        });

        var sp = services.BuildServiceProvider();
        var app = sp.GetRequiredService<TerminaApplication>();
        tuiNavigation.Attach(app);

        dashboardVm = capturedDashboardVm!;
        getChannelsVm = () => capturedChannelsVm;
        return app;
    }
}
