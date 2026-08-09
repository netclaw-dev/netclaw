// -----------------------------------------------------------------------
// <copyright file="ModelManagerPageTests.cs" company="Petabridge, LLC">
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

public sealed class ModelManagerPageTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly FakeProviderProbe _fakeProbe = new();
    private readonly ProviderDescriptorRegistry _registry = ProviderCommand.CreateDefaultRegistry();

    public ModelManagerPageTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task InitialRoleOverview_ShowsConfiguredMainAndFallbackModels()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["deepseek-test"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai-compatible",
                    ["Endpoint"] = "https://api.deepseek.example"
                },
                ["big-gpu"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai-compatible",
                    ["Endpoint"] = "http://gpu.example"
                }
            },
            ["Models"] = new Dictionary<string, object>
            {
                ["Definitions"] = new Dictionary<string, object>
                {
                    ["deepseek-v4-flash"] = new Dictionary<string, object>
                    {
                        ["Provider"] = "deepseek-test",
                        ["ModelId"] = "deepseek-v4-flash"
                    },
                    ["fallback-model"] = new Dictionary<string, object>
                    {
                        ["Provider"] = "big-gpu",
                        ["ModelId"] = "fallback-model"
                    }
                },
                ["Roles"] = new Dictionary<string, object>
                {
                    ["Main"] = "deepseek-v4-flash",
                    ["Fallback"] = "fallback-model"
                }
            }
        });

        var (terminal, app) = CreateHeadlessApp(out var input);

        using var appCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = app.RunAsync(appCts.Token);

        try
        {
            using var overviewCts = CancellationTokenSource.CreateLinkedTokenSource(appCts.Token);
            overviewCts.CancelAfter(TimeSpan.FromSeconds(2));
            await WaitForConditionAsync(
                () => terminal.Contains("deepseek-test")
                      && terminal.Contains("deepseek-v4-flash")
                      && terminal.Contains("big-gpu")
                      && terminal.Contains("fallback-model"),
                overviewCts.Token);
        }
        finally
        {
            input.EnqueueKey(ConsoleKey.Q, control: true);
            await run.WaitAsync(appCts.Token);
        }
    }

    [Fact]
    public async Task ModelAssignment_WhenTheUserChangesProvider_ShowsTheSelectedProvidersModels()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["alpha-openai"] = new Dictionary<string, object> { ["Type"] = "openai" },
                ["bravo-copilot"] = new Dictionary<string, object> { ["Type"] = "github-copilot" },
                ["charlie-deepseek"] = new Dictionary<string, object> { ["Type"] = "deepseek" }
            }
        });
        _fakeProbe.TypeResults["openai"] = SuccessfulProbe("openai-model");
        _fakeProbe.TypeResults["github-copilot"] = SuccessfulProbe("copilot-model");
        _fakeProbe.TypeResults["deepseek"] = SuccessfulProbe("deepseek-model");

        var (terminal, app) = CreateHeadlessApp(out var input);

        using var appCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = app.RunAsync(appCts.Token);

        try
        {
            await WaitForConditionAsync(() => terminal.Contains("Main"), appCts.Token);

            input.EnqueueKey(ConsoleKey.Enter);
            await WaitForConditionAsync(() => terminal.Contains("alpha-openai"), appCts.Token);
            input.EnqueueKey(ConsoleKey.Enter);
            await WaitForConditionAsync(() => terminal.Contains("openai-model"), appCts.Token);

            input.EnqueueKey(ConsoleKey.Escape);
            await WaitForConditionAsync(() => terminal.Contains("Select provider for Main"), appCts.Token);
            input.EnqueueKey(ConsoleKey.DownArrow);
            input.EnqueueKey(ConsoleKey.Enter);
            await WaitForConditionAsync(
                () => terminal.Contains("bravo-copilot") && terminal.Contains("copilot-model"),
                appCts.Token);

            input.EnqueueKey(ConsoleKey.Escape);
            await WaitForConditionAsync(() => terminal.Contains("Select provider for Main"), appCts.Token);
            input.EnqueueKey(ConsoleKey.DownArrow);
            input.EnqueueKey(ConsoleKey.DownArrow);
            input.EnqueueKey(ConsoleKey.Enter);
            await WaitForConditionAsync(
                () => terminal.Contains("charlie-deepseek") && terminal.Contains("deepseek-model"),
                appCts.Token);
        }
        finally
        {
            input.EnqueueKey(ConsoleKey.Q, control: true);
            await run.WaitAsync(appCts.Token);
        }
    }

    private (VirtualTerminal Terminal, TerminaApplication App) CreateHeadlessApp(
        out VirtualInputSource input)
    {
        var terminal = new VirtualTerminal(160, 40);
        var virtualInput = new VirtualInputSource();
        input = virtualInput;

        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminal);
        services.AddTerminaVirtualInput(virtualInput);
        services.AddTermina("/model", builder =>
        {
            builder.RegisterRoute<ModelManagerPage, ModelManagerViewModel>(
                "/model",
                _ => new ModelManagerPage(),
                _ => new ModelManagerViewModel(_paths, _fakeProbe, _registry));
        });

        var serviceProvider = services.BuildServiceProvider();
        return (terminal, serviceProvider.GetRequiredService<TerminaApplication>());
    }

    private void WriteConfig(Dictionary<string, object> data)
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static ProviderProbeResult SuccessfulProbe(string modelId)
        => new(true, null, [new DiscoveredModel { ModelId = new ModelId(modelId) }]);

    private static async Task WaitForConditionAsync(Func<bool> predicate, CancellationToken ct)
    {
        while (!predicate())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
}
