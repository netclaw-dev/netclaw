// -----------------------------------------------------------------------
// <copyright file="ProviderManagerPageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
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
    public async Task Escape_AtRoot_DoesNotQuit()
    {
        // Regression for #1764: in standalone `netclaw provider`, Escape at the
        // root used to Shutdown(). It must be a no-op; only Ctrl+Q quits.
        // Proof: the list survives Escape and a subsequent Delete still works.
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["alpha-ollama"] = new Dictionary<string, object>
                {
                    ["Type"] = "ollama",
                    ["Endpoint"] = "http://localhost:11434",
                    ["AuthMethod"] = "None"
                },
                ["bravo-ollama"] = new Dictionary<string, object>
                {
                    ["Type"] = "ollama",
                    ["Endpoint"] = "http://localhost:11435",
                    ["AuthMethod"] = "None"
                }
            }
        });

        var (_, app, vm) = CreateHeadlessApp(out var input);

        input.EnqueueKey(ConsoleKey.Escape);    // must be a no-op at root
        input.EnqueueKey(ConsoleKey.DownArrow); // move highlight off row 0
        input.EnqueueKey(ConsoleKey.Delete);    // start remove for highlighted row
        input.EnqueueKey(ConsoleKey.Enter);     // confirm "Yes, remove"
        input.EnqueueKey(ConsoleKey.Q, control: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.DoesNotContain(vm.DisplayProviders, p => p.ConfiguredName == "bravo-ollama");
    }

    [Fact]
    public async Task GitHubCopilotEnterpriseInputs_AcceptTypedHostAndApiBase()
    {
        var (_, app, vm) = CreateHeadlessApp(out var input);

        foreach (var _ in _registry.KnownTypeKeys.TakeWhile(type => type != "github-copilot"))
            input.EnqueueKey(ConsoleKey.DownArrow);
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

    [Fact]
    public async Task DeleteKey_OnSecondRow_RemovesHighlightedProvider()
    {
        // Seed two configured providers so the list has multiple configured rows.
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["alpha-ollama"] = new Dictionary<string, object>
                {
                    ["Type"] = "ollama",
                    ["Endpoint"] = "http://localhost:11434",
                    ["AuthMethod"] = "None"
                },
                ["bravo-ollama"] = new Dictionary<string, object>
                {
                    ["Type"] = "ollama",
                    ["Endpoint"] = "http://localhost:11435",
                    ["AuthMethod"] = "None"
                }
            }
        });

        var (_, app, vm) = CreateHeadlessApp(out var input);

        input.EnqueueKey(ConsoleKey.DownArrow); // move highlight off row 0 -> bravo-ollama
        input.EnqueueKey(ConsoleKey.Delete);    // start remove for highlighted row
        input.EnqueueKey(ConsoleKey.Enter);     // confirm "Yes, remove"
        input.EnqueueKey(ConsoleKey.Q, control: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        // The highlighted row (bravo-ollama) was removed; the un-highlighted row
        // (alpha-ollama) must survive. This asserts Delete targets the live
        // highlight, not a stale SelectedProviderIndex stuck on row 0.
        Assert.Contains(vm.DisplayProviders, p => p.ConfiguredName == "alpha-ollama");
        Assert.DoesNotContain(vm.DisplayProviders, p => p.ConfiguredName == "bravo-ollama");
    }

    private void WriteConfig(Dictionary<string, object> data)
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }
}
