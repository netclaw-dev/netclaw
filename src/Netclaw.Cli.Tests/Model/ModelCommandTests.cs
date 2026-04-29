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
        Assert.Equal(32768, main.GetProperty("ContextWindow").GetInt32());
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
