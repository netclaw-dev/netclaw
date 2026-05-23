// -----------------------------------------------------------------------
// <copyright file="ModelCommandTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Model;
using Netclaw.Cli.Tests.Tui;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Model;

public sealed class ModelCommandTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly FakeProviderProbe _fakeProbe = new();
    private readonly StringWriter _output = new();

    public ModelCommandTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        _output.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task List_NoConfig_ShowsEmptyMessage()
    {
        await ModelCommand.RunAsync(["model", "list"], _paths, output: _output);

        Assert.Contains("No models configured", _output.ToString());
    }

    [Fact]
    public async Task List_WithConfig_ShowsConfiguredModels()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Models"] = new Dictionary<string, object>
            {
                ["Main"] = new Dictionary<string, object>
                {
                    ["Provider"] = "my-ollama",
                    ["ModelId"] = "qwen3:30b"
                }
            }
        });

        await ModelCommand.RunAsync(["model", "list"], _paths, output: _output);
        var output = _output.ToString();

        Assert.Contains("Main", output);
        Assert.Contains("my-ollama", output);
        Assert.Contains("qwen3:30b", output);
    }

    [Fact]
    public async Task Set_MainModel_WritesConfig()
    {
        // Arrange: need a provider to exist
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-ollama"] = new Dictionary<string, object>
                {
                    ["Type"] = "ollama",
                    ["Endpoint"] = "http://localhost:11434"
                }
            }
        });

        var exitCode = await ModelCommand.RunAsync(
            ["model", "set", "main", "my-ollama", "qwen3:30b", "--context-window", "32768"],
            _paths, output: _output);

        Assert.Equal(0, exitCode);

        var config = ReadConfigFile(_paths.NetclawConfigPath);
        Assert.True(config.RootElement.TryGetProperty("Models", out var models));
        Assert.True(models.TryGetProperty("Main", out var main));
        Assert.Equal("my-ollama", main.GetProperty("Provider").GetString());
        Assert.Equal("qwen3:30b", main.GetProperty("ModelId").GetString());
        Assert.Equal("Manual", main.GetProperty("Provenance").GetString());

        // Role records are identity-only; --context-window writes to Catalog
        // keyed by "{provider}/{modelId}" so the override survives later role
        // swaps. Catalog key contains ':' (Ollama's `qwen3:30b`) — this
        // assertion also pins down the IConfiguration colon-split fix, since
        // LoadModelSelection reads back via JsonSerializer.
        Assert.False(main.TryGetProperty("ContextWindow", out _));
        var catalog = models.GetProperty("Catalog");
        var entry = catalog.GetProperty("my-ollama/qwen3:30b");
        Assert.Equal(32768, entry.GetProperty("ContextWindow").GetInt32());

        var selection = ModelCommand.LoadModelSelection(_paths)!;
        Assert.Equal(32768, selection.Main.ContextWindow);
    }

    [Fact]
    public async Task Set_InvalidRole_ReturnsError()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-ollama"] = new Dictionary<string, object>
                {
                    ["Type"] = "ollama"
                }
            }
        });

        var exitCode = await ModelCommand.RunAsync(
            ["model", "set", "invalid-role", "my-ollama", "qwen3:30b"],
            _paths, output: _output);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Set_NonexistentProvider_ReturnsError()
    {
        var exitCode = await ModelCommand.RunAsync(
            ["model", "set", "main", "nonexistent", "qwen3:30b"],
            _paths, output: _output);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Discover_ValidProvider_ShowsModels()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-ollama"] = new Dictionary<string, object>
                {
                    ["Type"] = "ollama",
                    ["Endpoint"] = "http://localhost:11434"
                }
            }
        });

        var exitCode = await ModelCommand.RunAsync(
            ["model", "discover", "my-ollama"],
            _paths, _fakeProbe, output: _output);

        Assert.Equal(0, exitCode);
        var output = _output.ToString();

        Assert.Contains("model-a", output);
        Assert.Contains("model-b", output);
        Assert.Contains("2 model(s) found", output);
        Assert.Equal(1, _fakeProbe.ProbeCallCount);
    }

    [Fact]
    public async Task Discover_OAuthProvider_UsesOAuthAccessToken()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-openai"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai",
                    ["Endpoint"] = "https://api.openai.com",
                    ["AuthMethod"] = "OAuthDevice"
                }
            }
        });

        WriteSecrets(new Dictionary<string, object>
        {
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-openai"] = new Dictionary<string, object>
                {
                    ["OAuthAccessToken"] = "oauth-access-token"
                }
            }
        });

        var exitCode = await ModelCommand.RunAsync(
            ["model", "discover", "my-openai"],
            _paths, _fakeProbe, output: _output);

        Assert.Equal(0, exitCode);
        Assert.Equal("oauth-access-token", _fakeProbe.LastApiKey);
    }

    [Fact]
    public async Task Discover_NonexistentProvider_ReturnsError()
    {
        var exitCode = await ModelCommand.RunAsync(
            ["model", "discover", "nonexistent"],
            _paths, _fakeProbe, output: _output);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Clear_Fallback_RemovesFromConfig()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Models"] = new Dictionary<string, object>
            {
                ["Main"] = new Dictionary<string, object>
                {
                    ["Provider"] = "my-ollama",
                    ["ModelId"] = "qwen3:30b"
                },
                ["Fallback"] = new Dictionary<string, object>
                {
                    ["Provider"] = "my-openrouter",
                    ["ModelId"] = "meta-llama/llama-3-70b"
                }
            }
        });

        var exitCode = await ModelCommand.RunAsync(
            ["model", "clear", "fallback"],
            _paths, output: _output);

        Assert.Equal(0, exitCode);

        var config = ReadConfigFile(_paths.NetclawConfigPath);
        Assert.True(config.RootElement.TryGetProperty("Models", out var models));
        Assert.True(models.TryGetProperty("Main", out _)); // Main still exists
        Assert.False(models.TryGetProperty("Fallback", out _)); // Fallback removed
    }

    [Fact]
    public async Task Clear_Main_ReturnsError()
    {
        var exitCode = await ModelCommand.RunAsync(
            ["model", "clear", "main"],
            _paths, output: _output);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Set_FallbackModel_WritesCorrectRole()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-ollama"] = new Dictionary<string, object> { ["Type"] = "ollama" }
            },
            ["Models"] = new Dictionary<string, object>
            {
                ["Main"] = new Dictionary<string, object>
                {
                    ["Provider"] = "my-ollama",
                    ["ModelId"] = "qwen3:30b"
                }
            }
        });

        var exitCode = await ModelCommand.RunAsync(
            ["model", "set", "fallback", "my-ollama", "qwen3:8b"],
            _paths, output: _output);

        Assert.Equal(0, exitCode);

        var config = ReadConfigFile(_paths.NetclawConfigPath);
        var models = config.RootElement.GetProperty("Models");
        Assert.True(models.TryGetProperty("Main", out _)); // Main preserved
        Assert.True(models.TryGetProperty("Fallback", out var fallback));
        Assert.Equal("qwen3:8b", fallback.GetProperty("ModelId").GetString());
    }

    [Fact]
    public async Task Set_ContextWindowFlag_WritesToCatalog_NotInlineRole()
    {
        // The role record stays a pure identity pointer; explicit override
        // intent (--context-window) lands in Models.Catalog so it survives
        // role-pointer changes (the #1127 contract).
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["p"] = new Dictionary<string, object> { ["Type"] = "ollama" }
            }
        });

        await ModelCommand.RunAsync(
            ["model", "set", "main", "p", "m", "--context-window", "200000"],
            _paths, output: _output);

        var config = ReadConfigFile(_paths.NetclawConfigPath);
        var models = config.RootElement.GetProperty("Models");
        var main = models.GetProperty("Main");
        Assert.False(main.TryGetProperty("ContextWindow", out _),
            "Role record should not carry inline ContextWindow — overrides live in Catalog.");

        var entry = models.GetProperty("Catalog").GetProperty("p/m");
        Assert.Equal(200_000, entry.GetProperty("ContextWindow").GetInt32());

        // Effective via overlay merge: the role sees ContextWindow=200000.
        var selection = ModelCommand.LoadModelSelection(_paths)!;
        Assert.Equal(200_000, selection.Main.ContextWindow);
    }

    [Fact]
    public async Task Set_SwitchingRole_PointerDoesNotTouchCatalog()
    {
        // #1127 core invariant: changing Main from p/m1 to p/m2 does NOT
        // disturb Models.Catalog. The hand-set override on p/m1 stays in
        // place and re-applies the next time Main points back at p/m1.
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["p"] = new Dictionary<string, object> { ["Type"] = "ollama" }
            }
        });

        // Operator pins ContextWindow=200000 on p/m1.
        await ModelCommand.RunAsync(
            ["model", "set", "main", "p", "m1", "--context-window", "200000"],
            _paths, output: _output);

        // Operator switches Main to p/m2 (no override on m2).
        await ModelCommand.RunAsync(
            ["model", "set", "main", "p", "m2"],
            _paths, output: _output);

        var afterSwitch = ModelCommand.LoadModelSelection(_paths)!;
        Assert.Equal("m2", afterSwitch.Main.ModelId);
        Assert.Null(afterSwitch.Main.ContextWindow); // no override for m2

        // Catalog still holds the m1 override untouched.
        var config = ReadConfigFile(_paths.NetclawConfigPath);
        var entry = config.RootElement
            .GetProperty("Models").GetProperty("Catalog").GetProperty("p/m1");
        Assert.Equal(200_000, entry.GetProperty("ContextWindow").GetInt32());

        // Switching Main back to p/m1 re-applies the saved override.
        await ModelCommand.RunAsync(
            ["model", "set", "main", "p", "m1"],
            _paths, output: _output);

        var afterReturn = ModelCommand.LoadModelSelection(_paths)!;
        Assert.Equal(200_000, afterReturn.Main.ContextWindow);
    }

    [Fact]
    public async Task Clear_LeavesCatalogIntact()
    {
        // Clearing a role removes the pointer; saved overrides survive.
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["p"] = new Dictionary<string, object> { ["Type"] = "ollama" }
            },
            ["Models"] = new Dictionary<string, object>
            {
                ["Main"] = new Dictionary<string, object>
                {
                    ["Provider"] = "p",
                    ["ModelId"] = "main-model"
                },
                ["Fallback"] = new Dictionary<string, object>
                {
                    ["Provider"] = "p",
                    ["ModelId"] = "fallback-model"
                },
                ["Catalog"] = new Dictionary<string, object>
                {
                    ["p/fallback-model"] = new Dictionary<string, object>
                    {
                        ["ContextWindow"] = 65536L
                    }
                }
            }
        });

        await ModelCommand.RunAsync(["model", "clear", "fallback"], _paths, output: _output);
        await ModelCommand.RunAsync(
            ["model", "set", "fallback", "p", "fallback-model"], _paths, output: _output);

        var selection = ModelCommand.LoadModelSelection(_paths)!;
        Assert.Equal(65_536, selection.Fallback!.ContextWindow);
    }

    private void WriteConfig(Dictionary<string, object> data)
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void WriteSecrets(Dictionary<string, object> data)
    {
        File.WriteAllText(_paths.SecretsPath,
            JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static JsonDocument ReadConfigFile(string path)
    {
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}
