// -----------------------------------------------------------------------
// <copyright file="ProviderManagerPageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Cli.Provider;
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Tests.Utilities;
using Termina;
using Termina.Hosting;
using Termina.Input;
using Termina.Terminal;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

public sealed class ProviderManagerPageTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly FakeProviderProbe _fakeProbe = new();
    private readonly ProviderDescriptorRegistry _registry = ProviderCommand.CreateDefaultRegistry();

    public ProviderManagerPageTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task GitHubCopilotEnterpriseInputs_AcceptTypedHostAndApiBase()
    {
        var (_, app, vm) = CreateHeadlessApp(out var input);

        input.EnqueueKey(ConsoleKey.DownArrow); // GitHub Copilot
        input.EnqueueKey(ConsoleKey.Enter);     // type row -> Name your provider
        input.EnqueueKey(ConsoleKey.Enter);     // accept generated provider name
        input.EnqueueKey(ConsoleKey.Enter);     // OAuth Device Flow
        input.EnqueueKey(ConsoleKey.DownArrow); // GitHub Enterprise
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueuePaste("https://ghe.example.com");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueuePaste("https://api.ghe.example.com/");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, control: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal("github-copilot", vm.NewProviderType);
        Assert.Equal("https://ghe.example.com", vm.NewGitHubCopilotHost);
        Assert.Equal("https://api.ghe.example.com/", vm.NewGitHubCopilotApiBase);
        Assert.NotNull(vm.NewVendorOptions);
        Assert.Equal("https://ghe.example.com", vm.NewVendorOptions!["GitHubHost"]);
        Assert.Equal("https://api.ghe.example.com", vm.NewVendorOptions["GitHubApiBase"]);
    }

    private (VirtualTerminal Terminal, TerminaApplication App, ProviderManagerViewModel Vm)
        CreateHeadlessApp(out VirtualInputSource input)
    {
        var terminal = new VirtualTerminal(120, 40);
        var virtualInput = new VirtualInputSource();
        input = virtualInput;

        ProviderManagerViewModel? capturedVm = null;

        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminal);
        services.AddTerminaVirtualInput(virtualInput);
        services.AddTermina("/provider", builder =>
        {
            builder.RegisterRoute<ProviderManagerPage, ProviderManagerViewModel>(
                "/provider",
                _ => new ProviderManagerPage(),
                _ =>
                {
                    capturedVm = new ProviderManagerViewModel(_paths, _registry, _fakeProbe);
                    return capturedVm;
                });
        });

        var sp = services.BuildServiceProvider();
        var app = sp.GetRequiredService<TerminaApplication>();

        return (terminal, app, capturedVm!);
    }
}
