// -----------------------------------------------------------------------
// <copyright file="SearchConfigEditorPageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Cli.Tui.Config;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Termina;
using Termina.Hosting;
using Termina.Input;
using Termina.Terminal;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

public sealed class SearchConfigEditorPageTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public SearchConfigEditorPageTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        File.WriteAllText(_paths.NetclawConfigPath, """
            {
              "configVersion": 1,
              "Search": {
                "Backend": "duckduckgo"
              }
            }
            """);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task ProviderSelection_RendersActiveAndConfiguredLegend()
    {
        var (terminal, app, _) = CreateHeadlessApp(out var input);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.True(terminal.Contains("(*) active backend"),
            $"Expected active-backend legend in terminal output. Screen:\n{terminal}");
        Assert.True(terminal.Contains("backend has saved setup"),
            $"Expected configured-backend legend in terminal output. Screen:\n{terminal}");
    }

    [Fact]
    public async Task SavedScreen_EscapeReturnsToProviderSelection()
    {
        var (terminal, app, vm) = CreateHeadlessApp(out var input);

        vm.SaveWithoutProbeOverride();

        input.EnqueueKey(ConsoleKey.Escape);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(SearchConfigEditorScreen.ProviderSelection, vm.CurrentScreen.Value);
        Assert.True(terminal.Contains("Choose the backend Netclaw uses for web search."),
            $"Expected provider selection screen after Esc from saved state. Screen:\n{terminal}");
    }

    private (VirtualTerminal Terminal, TerminaApplication App, SearchConfigEditorViewModel Vm)
        CreateHeadlessApp(out VirtualInputSource input)
    {
        var terminal = new VirtualTerminal(120, 40);
        var virtualInput = new VirtualInputSource();
        input = virtualInput;

        SearchConfigEditorViewModel? capturedVm = null;

        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminal);
        services.AddTerminaVirtualInput(virtualInput);
        services.AddTermina("/search", builder =>
        {
            builder.RegisterRoute<SearchConfigEditorPage, SearchConfigEditorViewModel>(
                "/search",
                _ => new SearchConfigEditorPage(),
                _ =>
                {
                    capturedVm = new SearchConfigEditorViewModel(_paths);
                    return capturedVm;
                });
        });

        var sp = services.BuildServiceProvider();
        var app = sp.GetRequiredService<TerminaApplication>();

        return (terminal, app, capturedVm!);
    }
}
