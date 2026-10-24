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
    public async Task RoleOverview_At120Columns_ShowsFullModelIdAndAlignsHeaderWithRows()
    {
        const string longModelId = "Qwen3.6-35B-A3B-UD-Q4_K_XL-gguf-v2-final";
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["openai-compatible"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai-compatible",
                    ["Endpoint"] = "http://gateway.example.test:8080/v1"
                }
            },
            ["Models"] = new Dictionary<string, object>
            {
                ["Definitions"] = new Dictionary<string, object>
                {
                    ["main"] = new Dictionary<string, object>
                    {
                        ["Provider"] = "openai-compatible",
                        ["ModelId"] = longModelId
                    }
                },
                ["Roles"] = new Dictionary<string, object> { ["Main"] = "main" }
            }
        });

        var (terminal, app) = CreateHeadlessApp(out var input, width: 120);
        input.EnqueueKey(ConsoleKey.Q, control: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var lines = terminal.GetAllLines();
        var header = lines.Single(l => l.Contains("Role") && l.Contains("Model ID"));
        var row = lines.Single(l => l.Contains("Main") && l.Contains("openai-compatible"));

        // Long provider label is capped, so the model ID gets the room and is not cut.
        Assert.Contains(longModelId, row);
        Assert.Equal(header.IndexOf("Role", StringComparison.Ordinal), row.IndexOf("Main", StringComparison.Ordinal));
        Assert.Equal(header.IndexOf("Provider", StringComparison.Ordinal),
            row.IndexOf("openai-compatible (", StringComparison.Ordinal));
        Assert.Equal(header.IndexOf("Model ID", StringComparison.Ordinal),
            row.IndexOf(longModelId, StringComparison.Ordinal));
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

    [Fact]
    public async Task ClearRole_PressCAfterArrowNavigation_RemovesRoleFromConfigAndTable()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["spark-acad"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai-compatible",
                    ["Endpoint"] = "http://acad.example:8080/v1"
                }
            },
            ["Models"] = new Dictionary<string, object>
            {
                ["Definitions"] = new Dictionary<string, object>
                {
                    ["main-model"] = new Dictionary<string, object>
                    {
                        ["Provider"] = "spark-acad",
                        ["ModelId"] = "Qwen3.8-Flash-Next"
                    },
                    ["compaction-model"] = new Dictionary<string, object>
                    {
                        ["Provider"] = "spark-acad",
                        ["ModelId"] = "GLM-5.3-Flash-EXL3"
                    }
                },
                ["Roles"] = new Dictionary<string, object>
                {
                    ["Main"] = "main-model",
                    ["Compaction"] = "compaction-model"
                }
            }
        });

        var (terminal, app) = CreateHeadlessApp(out var input, width: 120);

        using var appCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = app.RunAsync(appCts.Token);

        try
        {
            await WaitForConditionAsync(() => terminal.Contains("GLM-5.3-Flash-EXL3"), appCts.Token);

            // Highlight Compaction with the arrows only. No Enter, so nothing is "selected".
            input.EnqueueKey(ConsoleKey.DownArrow);
            input.EnqueueKey(ConsoleKey.DownArrow);
            input.EnqueueKey(ConsoleKey.C);

            await WaitForConditionAsync(() => terminal.Contains("Cleared Compaction role"), appCts.Token);

            var roles = LoadRoles();
            Assert.True(roles.TryGetProperty("Main", out _));
            Assert.False(roles.TryGetProperty("Compaction", out _));

            // The table repaints: the cleared model row is gone, Main survives.
            await WaitForConditionAsync(
                () => !terminal.Contains("GLM-5.3-Flash-EXL3") && terminal.Contains("Qwen3.8-Flash-Next"),
                appCts.Token);

            // Press C again without moving. The rebuild must have kept the cursor on the
            // Compaction row, so this reports the role as unset rather than acting on Main.
            input.EnqueueKey(ConsoleKey.C);
            await WaitForConditionAsync(
                () => terminal.Contains("Compaction role is not set"), appCts.Token);
            Assert.True(LoadRoles().TryGetProperty("Main", out _));
        }
        finally
        {
            input.EnqueueKey(ConsoleKey.Q, control: true);
            await run.WaitAsync(appCts.Token);
        }
    }

    [Fact]
    public async Task ClearRole_AfterEnterSelectingThenBackingOut_ClearsSameRow()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["alpha"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai-compatible",
                    ["Endpoint"] = "http://alpha.example/v1"
                },
                ["bravo"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai-compatible",
                    ["Endpoint"] = "http://bravo.example/v1"
                }
            },
            ["Models"] = new Dictionary<string, object>
            {
                ["Definitions"] = new Dictionary<string, object>
                {
                    ["main-model"] = new Dictionary<string, object>
                    {
                        ["Provider"] = "alpha",
                        ["ModelId"] = "main-id"
                    },
                    ["fallback-model"] = new Dictionary<string, object>
                    {
                        ["Provider"] = "alpha",
                        ["ModelId"] = "fallback-id"
                    }
                },
                ["Roles"] = new Dictionary<string, object>
                {
                    ["Main"] = "main-model",
                    ["Fallback"] = "fallback-model"
                }
            }
        });

        var (terminal, app) = CreateHeadlessApp(out var input, width: 120);

        using var appCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = app.RunAsync(appCts.Token);

        try
        {
            await WaitForConditionAsync(() => terminal.Contains("fallback-id"), appCts.Token);

            // Enter on Fallback opens the provider picker; Escape returns to the table.
            // The rebuild must carry the cursor on Fallback, so C clears it next.
            input.EnqueueKey(ConsoleKey.DownArrow);
            input.EnqueueKey(ConsoleKey.Enter);
            await WaitForConditionAsync(() => terminal.Contains("Select provider for Fallback"), appCts.Token);
            input.EnqueueKey(ConsoleKey.Escape);
            await WaitForConditionAsync(() => terminal.Contains("Model Role Assignments"), appCts.Token);

            input.EnqueueKey(ConsoleKey.C);
            await WaitForConditionAsync(() => terminal.Contains("Cleared Fallback role"), appCts.Token);

            var roles = LoadRoles();
            Assert.True(roles.TryGetProperty("Main", out _));
            Assert.False(roles.TryGetProperty("Fallback", out _));
        }
        finally
        {
            input.EnqueueKey(ConsoleKey.Q, control: true);
            await run.WaitAsync(appCts.Token);
        }
    }

    [Fact]
    public async Task ClearRole_OnUnsetRole_ReportsWhyNothingChanged()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["spark-acad"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai-compatible",
                    ["Endpoint"] = "http://acad.example:8080/v1"
                }
            },
            ["Models"] = new Dictionary<string, object>
            {
                ["Definitions"] = new Dictionary<string, object>
                {
                    ["main-model"] = new Dictionary<string, object>
                    {
                        ["Provider"] = "spark-acad",
                        ["ModelId"] = "Qwen3.8-Flash-Next"
                    }
                },
                ["Roles"] = new Dictionary<string, object> { ["Main"] = "main-model" }
            }
        });

        var (terminal, app) = CreateHeadlessApp(out var input, width: 120);

        using var appCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = app.RunAsync(appCts.Token);

        try
        {
            await WaitForConditionAsync(() => terminal.Contains("Qwen3.8-Flash-Next"), appCts.Token);

            // Cursor starts on Main; the reject message must name the reason.
            input.EnqueueKey(ConsoleKey.C);
            await WaitForConditionAsync(() => terminal.Contains("Cannot clear the main model role"), appCts.Token);

            input.EnqueueKey(ConsoleKey.DownArrow);
            input.EnqueueKey(ConsoleKey.C);
            await WaitForConditionAsync(
                () => terminal.Contains("Fallback role is not set"),
                appCts.Token);
        }
        finally
        {
            input.EnqueueKey(ConsoleKey.Q, control: true);
            await run.WaitAsync(appCts.Token);
        }
    }

    private JsonElement LoadRoles()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        return document.RootElement.GetProperty("Models").GetProperty("Roles").Clone();
    }

    private (VirtualTerminal Terminal, TerminaApplication App) CreateHeadlessApp(
        out VirtualInputSource input, int width = 160)
    {
        var terminal = new VirtualTerminal(width, 40);
        var virtualInput = new VirtualInputSource();
        input = virtualInput;

        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminal);
        services.AddTerminaVirtualInput(virtualInput);
        services.AddTermina("/model", builder =>
        {
            builder.RegisterRoute<ModelManagerPage, ModelManagerViewModel>(
                "/model",
                _ => new ModelManagerPage(terminal),
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
