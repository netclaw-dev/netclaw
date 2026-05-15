// -----------------------------------------------------------------------
// <copyright file="ProviderRenamerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Provider;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Provider;

public sealed class ProviderRenamerTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public ProviderRenamerTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Rename_SwapsKeyInConfigAndSecrets()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-vllm"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai-compatible",
                    ["Endpoint"] = "http://localhost:8080",
                    ["AuthMethod"] = "ApiKey"
                }
            }
        });

        WriteSecrets(new Dictionary<string, object>
        {
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-vllm"] = new Dictionary<string, object>
                {
                    ["ApiKey"] = "sk-fake"
                }
            }
        });

        var result = ProviderRenamer.Rename(_paths, "my-vllm", "lab-a100");

        Assert.True(result.Success);
        Assert.Null(result.ErrorMessage);

        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var providers = config.RootElement.GetProperty("Providers");
        Assert.False(providers.TryGetProperty("my-vllm", out _));
        Assert.True(providers.TryGetProperty("lab-a100", out var entry));
        Assert.Equal("openai-compatible", entry.GetProperty("Type").GetString());
        Assert.Equal("http://localhost:8080", entry.GetProperty("Endpoint").GetString());

        var secrets = JsonDocument.Parse(File.ReadAllText(_paths.SecretsPath));
        var secretProviders = secrets.RootElement.GetProperty("Providers");
        Assert.False(secretProviders.TryGetProperty("my-vllm", out _));
        Assert.True(secretProviders.TryGetProperty("lab-a100", out _));
    }

    [Fact]
    public void Rename_NoSecretsEntry_StillSucceeds()
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

        var result = ProviderRenamer.Rename(_paths, "my-ollama", "lab-ollama");

        Assert.True(result.Success);

        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var providers = config.RootElement.GetProperty("Providers");
        Assert.True(providers.TryGetProperty("lab-ollama", out _));
    }

    [Fact]
    public void Rename_OldNameMissing_ReturnsError()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>()
        });

        var result = ProviderRenamer.Rename(_paths, "does-not-exist", "anything");

        Assert.False(result.Success);
        Assert.Contains("does-not-exist", result.ErrorMessage!);
    }

    [Fact]
    public void Rename_EmptyNewName_ReturnsError()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-vllm"] = new Dictionary<string, object> { ["Type"] = "openai-compatible" }
            }
        });

        var result = ProviderRenamer.Rename(_paths, "my-vllm", "   ");

        Assert.False(result.Success);
        Assert.NotEmpty(result.ErrorMessage!);
    }

    [Fact]
    public void Rename_CollidesWithExistingProvider_ReturnsError()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-vllm-a"] = new Dictionary<string, object> { ["Type"] = "openai-compatible" },
                ["my-vllm-b"] = new Dictionary<string, object> { ["Type"] = "openai-compatible" }
            }
        });

        var result = ProviderRenamer.Rename(_paths, "my-vllm-a", "my-vllm-b");

        Assert.False(result.Success);
        Assert.Contains("my-vllm-b", result.ErrorMessage!);
    }

    [Fact]
    public void Rename_CollisionCheckIsCaseInsensitive()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-vllm"] = new Dictionary<string, object> { ["Type"] = "openai-compatible" },
                ["my-ollama"] = new Dictionary<string, object> { ["Type"] = "ollama" }
            }
        });

        var result = ProviderRenamer.Rename(_paths, "my-vllm", "MY-OLLAMA");

        Assert.False(result.Success);
    }

    [Fact]
    public void Rename_CaseOnlyChange_RewritesKeyInPlace()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-vllm"] = new Dictionary<string, object> { ["Type"] = "openai-compatible" }
            }
        });

        var result = ProviderRenamer.Rename(_paths, "my-vllm", "My-Vllm");

        Assert.True(result.Success);

        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var providers = config.RootElement.GetProperty("Providers");
        Assert.True(providers.TryGetProperty("My-Vllm", out _));
        Assert.False(providers.TryGetProperty("my-vllm", out _));
    }

    [Fact]
    public void Rename_CascadesToAllMatchingModelRoles()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-vllm"] = new Dictionary<string, object> { ["Type"] = "openai-compatible" },
                ["my-ollama"] = new Dictionary<string, object> { ["Type"] = "ollama" }
            },
            ["Models"] = new Dictionary<string, object>
            {
                ["Main"] = new Dictionary<string, object>
                {
                    ["Provider"] = "my-vllm",
                    ["ModelId"] = "Qwen/Qwen3-30B"
                },
                ["Fallback"] = new Dictionary<string, object>
                {
                    ["Provider"] = "my-vllm",
                    ["ModelId"] = "Qwen/Qwen3-7B"
                },
                ["Compaction"] = new Dictionary<string, object>
                {
                    ["Provider"] = "my-ollama",
                    ["ModelId"] = "qwen3:7b"
                }
            }
        });

        var result = ProviderRenamer.Rename(_paths, "my-vllm", "lab-a100");

        Assert.True(result.Success);
        Assert.Equal(new[] { "Main", "Fallback" }, result.ReassignedModelRoles);

        using var doc = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var models = doc.RootElement.GetProperty("Models");
        Assert.Equal("lab-a100", models.GetProperty("Main").GetProperty("Provider").GetString());
        Assert.Equal("lab-a100", models.GetProperty("Fallback").GetProperty("Provider").GetString());
        Assert.Equal("my-ollama", models.GetProperty("Compaction").GetProperty("Provider").GetString());
    }

    [Fact]
    public void Rename_NoModelsSection_ReportsEmptyReassignments()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-vllm"] = new Dictionary<string, object> { ["Type"] = "openai-compatible" }
            }
        });

        var result = ProviderRenamer.Rename(_paths, "my-vllm", "lab-a100");

        Assert.True(result.Success);
        Assert.Empty(result.ReassignedModelRoles);
    }

    [Fact]
    public void Rename_CascadeIsCaseInsensitive()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-vllm"] = new Dictionary<string, object> { ["Type"] = "openai-compatible" }
            },
            ["Models"] = new Dictionary<string, object>
            {
                ["Main"] = new Dictionary<string, object>
                {
                    ["Provider"] = "MY-VLLM",
                    ["ModelId"] = "x"
                }
            }
        });

        var result = ProviderRenamer.Rename(_paths, "my-vllm", "lab-a100");

        Assert.True(result.Success);
        Assert.Contains("Main", result.ReassignedModelRoles);
    }

    [Fact]
    public void Rename_TrimsWhitespaceOnNewName()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-vllm"] = new Dictionary<string, object> { ["Type"] = "openai-compatible" }
            }
        });

        var result = ProviderRenamer.Rename(_paths, "my-vllm", "  lab-a100  ");

        Assert.True(result.Success);

        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var providers = config.RootElement.GetProperty("Providers");
        Assert.True(providers.TryGetProperty("lab-a100", out _));
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
}
