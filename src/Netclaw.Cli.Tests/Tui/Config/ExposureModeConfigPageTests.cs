// -----------------------------------------------------------------------
// <copyright file="ExposureModeConfigPageTests.cs" company="Petabridge, LLC">
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

public sealed class ExposureModeConfigPageTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public ExposureModeConfigPageTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        File.WriteAllText(_paths.NetclawConfigPath, """
            {
              "configVersion": 1,
              "Daemon": {
                "ExposureMode": "reverse-proxy",
                "Host": "10.0.0.5",
                "TrustedProxies": ["10.0.0.0/24"]
              }
            }
            """);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task ModeSelection_RendersActiveCheckboxForSavedExposureMode()
    {
        var (terminal, app, _) = CreateHeadlessApp(out var input);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.True(terminal.Contains("[x] active exposure mode"),
            $"Expected active exposure-mode legend in terminal output. Screen:\n{terminal}");
        Assert.True(terminal.Contains("[x] Reverse Proxy"),
            $"Expected saved reverse-proxy mode checkbox in terminal output. Screen:\n{terminal}");
    }

    private (VirtualTerminal Terminal, TerminaApplication App, ExposureModeConfigViewModel Vm)
        CreateHeadlessApp(out VirtualInputSource input)
    {
        var terminal = new VirtualTerminal(120, 40);
        var virtualInput = new VirtualInputSource();
        input = virtualInput;

        ExposureModeConfigViewModel? capturedVm = null;

        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminal);
        services.AddTerminaVirtualInput(virtualInput);
        services.AddTermina("/exposure", builder =>
        {
            builder.RegisterRoute<ExposureModeConfigPage, ExposureModeConfigViewModel>(
                "/exposure",
                _ => new ExposureModeConfigPage(),
                _ =>
                {
                    capturedVm = new ExposureModeConfigViewModel(_paths);
                    return capturedVm;
                });
        });

        var sp = services.BuildServiceProvider();
        var app = sp.GetRequiredService<TerminaApplication>();

        return (terminal, app, capturedVm!);
    }
}
