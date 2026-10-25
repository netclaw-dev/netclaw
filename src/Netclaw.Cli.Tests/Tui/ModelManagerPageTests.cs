// -----------------------------------------------------------------------
// <copyright file="ModelManagerPageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Configuration;
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

    // ── Properties panel (model-output-and-thinking-settings, tasks 2.11) ──
    //
    // Key contract of the panel:
    // - On the role table, P opens the properties panel of the highlighted role's definition. The
    //   panel shows the definition name and the label "Max output tokens".
    // - Focus starts on the limit field, and typed digits go to that field.
    // - Tab moves to the level picker. Its rows are default, off, low, medium, high, max, and the
    //   highlight starts on the current value.
    // - Enter saves the limit field and the highlighted level through the validator, and shows the
    //   status "Saved model properties". Esc closes the panel.
    // - A greyed level shows the reason from ThinkingDialectMap.OfferedLevels. Enter on a greyed
    //   level writes nothing and shows the status "Not saved: <reason>".

    private static readonly ThinkingSupport AllLevels = ThinkingSupport.Controllable(
        ThinkingLevel.Off, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.Max);

    private sealed record PanelRow(
        Dictionary<string, object> Providers,
        Dictionary<string, object> Definitions,
        Dictionary<string, object> Roles,
        Func<VirtualTerminal, VirtualInputSource, CancellationToken, Task> Script);

    private static Dictionary<string, object> Definition(string provider, string modelId, params (string Key, object Value)[] settings)
    {
        var definition = new Dictionary<string, object> { ["Provider"] = provider, ["ModelId"] = modelId };
        foreach (var (key, value) in settings)
            definition[key] = value;
        return definition;
    }

    private static readonly Dictionary<string, object> AlphaProvider = new()
    {
        ["alpha"] = new Dictionary<string, object> { ["Type"] = "openai-compatible", ["Endpoint"] = "http://alpha.example" },
    };

    private static readonly Dictionary<string, object> OllamaProvider = new()
    {
        ["local"] = new Dictionary<string, object> { ["Type"] = "ollama", ["Endpoint"] = "http://localhost:11434" },
    };

    public static TheoryData<string> PanelRowNames =>
    [
        "p_opens_properties_for_selected_role",
        "typed_limit_digits_reach_the_field",
        "save_valid_limit_persists_and_reloads",
        "unoffered_level_is_greyed_with_reason",
        "shared_definition_shows_also_used_by",
        "no_dialect_allows_only_output_field",
        "role_table_shows_max_out_and_thinking",
    ];

    private PanelRow PanelRowFor(string row) => row switch
    {
        "p_opens_properties_for_selected_role" => new(
            AlphaProvider,
            new() { ["main-def"] = Definition("alpha", "model-main"), ["fallback-def"] = Definition("alpha", "model-fallback") },
            new() { ["Main"] = "main-def", ["Fallback"] = "fallback-def" },
            async (terminal, input, ct) =>
            {
                input.EnqueueKey(ConsoleKey.DownArrow);
                input.EnqueueKey(ConsoleKey.P);
                await WaitForConditionAsync(
                    () => terminal.Contains("Max output tokens") && terminal.Contains("fallback-def"), ct);
            }),

        "typed_limit_digits_reach_the_field" => new(
            OllamaProvider,
            new() { ["main-def"] = Definition("local", "model-a") },
            new() { ["Main"] = "main-def" },
            async (terminal, input, ct) =>
            {
                await OpenPanelAsync(terminal, input, ct);
                // The role table shows "default 32000", so this row types another value.
                input.EnqueueString("24576");
                await WaitForConditionAsync(() => terminal.Contains("24576"), ct);
            }),

        "save_valid_limit_persists_and_reloads" => new(
            OllamaProvider,
            new() { ["main-def"] = Definition("local", "model-a") },
            new() { ["Main"] = "main-def" },
            async (terminal, input, ct) =>
            {
                await OpenPanelAsync(terminal, input, ct);
                input.EnqueueString("32000");
                input.EnqueueKey(ConsoleKey.Enter);
                await WaitForConditionAsync(() => terminal.Contains("Saved model properties"), ct);

                var main = ReloadMain();
                Assert.Equal(32000, main.MaxOutputTokens);
                Assert.Null(main.Thinking);
            }),

        "unoffered_level_is_greyed_with_reason" => new(
            new() { ["ds"] = new Dictionary<string, object> { ["Type"] = "deepseek" } },
            new() { ["ds-def"] = Definition("ds", "deepseek-v4") },
            new() { ["Main"] = "ds-def" },
            async (terminal, input, ct) =>
            {
                var offered = ThinkingDialectMap.OfferedLevels(ThinkingDialect.DeepSeek, AllLevels);
                Assert.True(offered.Unavailable.TryGetValue(ThinkingLevel.Medium, out var reason),
                    "The DeepSeek dialect must report a reason for medium.");
                var before = File.ReadAllBytes(_paths.NetclawConfigPath);

                await OpenPanelAsync(terminal, input, ct);
                await WaitForConditionAsync(() => terminal.Contains(reason), ct);

                input.EnqueueKey(ConsoleKey.Tab);
                input.EnqueueKey(ConsoleKey.DownArrow); // off
                input.EnqueueKey(ConsoleKey.DownArrow); // low
                input.EnqueueKey(ConsoleKey.DownArrow); // medium
                input.EnqueueKey(ConsoleKey.Enter);
                await WaitForConditionAsync(() => terminal.Contains("Not saved: " + reason), ct);
                Assert.Equal(before, File.ReadAllBytes(_paths.NetclawConfigPath));
            }),

        "shared_definition_shows_also_used_by" => new(
            OllamaProvider,
            new() { ["shared-def"] = Definition("local", "model-a") },
            new() { ["Main"] = "shared-def", ["Compaction"] = "shared-def" },
            async (terminal, input, ct) =>
            {
                await OpenPanelAsync(terminal, input, ct);
                await WaitForConditionAsync(() => terminal.Contains("Also used by: Compaction"), ct);
            }),

        "no_dialect_allows_only_output_field" => new(
            AlphaProvider,
            new() { ["gpu-def"] = Definition("alpha", "model-a") },
            new() { ["Main"] = "gpu-def" },
            async (terminal, input, ct) =>
            {
                await OpenPanelAsync(terminal, input, ct);
                await WaitForConditionAsync(() => terminal.Contains("ThinkingDialect"), ct);
                input.EnqueueString("16000");
                input.EnqueueKey(ConsoleKey.Enter);
                await WaitForConditionAsync(() => terminal.Contains("Saved model properties"), ct);

                var main = ReloadMain();
                Assert.Equal(16000, main.MaxOutputTokens);
                Assert.Null(main.Thinking);
            }),

        "role_table_shows_max_out_and_thinking" => new(
            OllamaProvider,
            new() { ["main-def"] = Definition("local", "model-a", ("MaxOutputTokens", 64000), ("Thinking", "Low")) },
            new() { ["Main"] = "main-def" },
            async (terminal, _, ct) =>
            {
                await WaitForConditionAsync(() => terminal.Contains("Max out") && terminal.Contains("Thinking"), ct);
                var lines = terminal.GetAllLines();
                var header = lines.Single(line => line.Contains("Role") && line.Contains("Model ID"));
                Assert.Contains("Max out", header);
                Assert.Contains("Thinking", header);
                var main = lines.Single(line => line.Contains("Main") && line.Contains("model-a"));
                Assert.Contains("64000", main);
                Assert.Contains("low", main);
            }),

        _ => throw new ArgumentOutOfRangeException(nameof(row), row, "Unknown row."),
    };

    [Theory]
    [MemberData(nameof(PanelRowNames))]
    public async Task Properties_panel_edits_the_definition_settings(string row)
    {
        var spec = PanelRowFor(row);
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = spec.Providers,
            ["Models"] = new Dictionary<string, object> { ["Definitions"] = spec.Definitions, ["Roles"] = spec.Roles },
        });
        WriteSecrets(new Dictionary<string, object>
        {
            ["Providers"] = new Dictionary<string, object> { ["ds"] = new Dictionary<string, object> { ["ApiKey"] = "sk-test" } },
        });
        // The probe reports the capability. The DeepSeek model accepts every level, so only the
        // dialect greys out a level.
        _fakeProbe.TypeResults["deepseek"] = new ProviderProbeResult(true, null,
            [new DiscoveredModel { ModelId = new ModelId("deepseek-v4"), Thinking = AllLevels }]);

        var (terminal, app) = CreateHeadlessApp(out var input, width: 120);
        using var appCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = app.RunAsync(appCts.Token);

        try
        {
            await WaitForConditionAsync(() => terminal.Contains("Model Role Assignments"), appCts.Token);
            await spec.Script(terminal, input, appCts.Token);
        }
        finally
        {
            input.EnqueueKey(ConsoleKey.Q, control: true);
            await run.WaitAsync(appCts.Token);
        }
    }

    private static async Task OpenPanelAsync(VirtualTerminal terminal, VirtualInputSource input, CancellationToken ct)
    {
        input.EnqueueKey(ConsoleKey.P);
        await WaitForConditionAsync(() => terminal.Contains("Max output tokens"), ct);
    }

    private ModelReference ReloadMain()
        => ModelConfigurationResolver.Resolve(new ConfigurationBuilder()
            .AddJsonFile(_paths.NetclawConfigPath, optional: false, reloadOnChange: false)
            .Build()).Selection.Main;

    private void WriteSecrets(Dictionary<string, object> data)
        => File.WriteAllText(_paths.SecretsPath, JsonSerializer.Serialize(data));

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
