// -----------------------------------------------------------------------
// <copyright file="Task1ConfigAreaPageTests.cs" company="Petabridge, LLC">
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

public sealed class Task1ConfigAreaPageTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public Task1ConfigAreaPageTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        File.WriteAllText(_paths.NetclawConfigPath, "{ \"configVersion\": 1 }");
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Workspaces_page_accepts_typed_and_pasted_path_input()
    {
        var app = CreateWorkspacesApp(out var input, out var vm);

        input.EnqueueString("/tmp/netclaw-");
        input.EnqueuePaste("workspace-test");
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal("/tmp/netclaw-workspace-test", vm.DirectoryDraft.Value);
    }

    [Fact]
    public async Task Inbound_webhooks_page_accepts_typed_timeout_input()
    {
        var app = CreateInboundWebhooksApp(out var input, out var vm);

        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueString("45");
        input.EnqueueKey(ConsoleKey.Backspace);
        input.EnqueuePaste("0");
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal("40", vm.TimeoutDraft.Value);
    }

    private TerminaApplication CreateWorkspacesApp(out VirtualInputSource input, out WorkspacesConfigViewModel vm)
    {
        var terminal = new VirtualTerminal(120, 40);
        var virtualInput = new VirtualInputSource();
        input = virtualInput;
        var capturedVm = new WorkspacesConfigViewModel(_paths);

        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminal);
        services.AddTerminaVirtualInput(virtualInput);
        services.AddTermina("/workspaces", builder =>
        {
            builder.RegisterRoute<WorkspacesConfigPage, WorkspacesConfigViewModel>(
                "/workspaces",
                _ => new WorkspacesConfigPage(),
                _ => capturedVm);
        });

        var sp = services.BuildServiceProvider();
        vm = capturedVm!;
        return sp.GetRequiredService<TerminaApplication>();
    }

    private TerminaApplication CreateInboundWebhooksApp(out VirtualInputSource input, out InboundWebhooksConfigViewModel vm)
    {
        var terminal = new VirtualTerminal(120, 40);
        var virtualInput = new VirtualInputSource();
        input = virtualInput;
        var capturedVm = new InboundWebhooksConfigViewModel(_paths);

        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminal);
        services.AddTerminaVirtualInput(virtualInput);
        services.AddTermina("/inbound-webhooks", builder =>
        {
            builder.RegisterRoute<InboundWebhooksConfigPage, InboundWebhooksConfigViewModel>(
                "/inbound-webhooks",
                _ => new InboundWebhooksConfigPage(),
                _ => capturedVm);
        });

        var sp = services.BuildServiceProvider();
        vm = capturedVm!;
        return sp.GetRequiredService<TerminaApplication>();
    }
}
