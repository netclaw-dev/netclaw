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
        var main = ReadActiveModel(config, "Main");
        Assert.Equal("my-ollama", main.GetProperty("Provider").GetString());
        Assert.Equal("qwen3:30b", main.GetProperty("ModelId").GetString());
        Assert.Equal("Manual", main.GetProperty("Provenance").GetString());
        Assert.Equal(32768, main.GetProperty("ContextWindow").GetInt32());
    }

    [Fact]
    public async Task Set_OpenAiOAuthModel_StoresLiveDiscoveredMetadata()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["openai-codex"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai",
                    ["AuthMethod"] = "OAuthDevice"
                }
            }
        });
        _fakeProbe.NextResult = new ProviderProbeResult(true, null,
        [
            new DiscoveredModel
            {
                ModelId = new Netclaw.Configuration.ModelId("gpt-new-codex"),
                ContextWindowTokens = 512000,
                InputModalities = ModelModality.Text | ModelModality.Image,
                OutputModalities = ModelModality.Text,
            }
        ]);

        var exitCode = await ModelCommand.RunAsync(
            ["model", "set", "main", "openai-codex", "gpt-new-codex"],
            _paths, _fakeProbe, output: _output);

        Assert.Equal(0, exitCode);
        var config = ReadConfigFile(_paths.NetclawConfigPath);
        var main = ReadActiveModel(config, "Main");
        Assert.Equal("Live", main.GetProperty("Provenance").GetString());
        Assert.Equal(512000, main.GetProperty("ContextWindow").GetInt32());
        Assert.Equal("Text, Image", main.GetProperty("InputModalities").GetString());
        Assert.Equal("Text", main.GetProperty("OutputModalities").GetString());
    }

    [Fact]
    public async Task Set_OpenAiOAuthModel_WhenProbeFails_ReturnsErrorWithoutWritingModel()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["openai-codex"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai",
                    ["AuthMethod"] = "OAuthDevice"
                }
            }
        });
        _fakeProbe.NextResult = new ProviderProbeResult(false, "Codex /models unavailable", []);

        var exitCode = await ModelCommand.RunAsync(
            ["model", "set", "main", "openai-codex", "gpt-new-codex"],
            _paths, _fakeProbe, output: _output);

        Assert.Equal(1, exitCode);
        Assert.Contains("Could not resolve model metadata", _output.ToString());
        var config = ReadConfigFile(_paths.NetclawConfigPath);
        Assert.False(config.RootElement.TryGetProperty("Models", out _));
    }

    [Fact]
    public async Task Set_OpenAiOAuthModel_WhenModelIsNotReturned_ReturnsErrorWithoutWritingModel()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["openai-codex"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai",
                    ["AuthMethod"] = "OAuthDevice"
                }
            }
        });
        _fakeProbe.NextResult = new ProviderProbeResult(true, null,
        [
            new DiscoveredModel { ModelId = new Netclaw.Configuration.ModelId("gpt-other-codex") }
        ]);

        var exitCode = await ModelCommand.RunAsync(
            ["model", "set", "main", "openai-codex", "gpt-new-codex"],
            _paths, _fakeProbe, output: _output);

        Assert.Equal(1, exitCode);
        Assert.Contains("was not returned", _output.ToString());
        var config = ReadConfigFile(_paths.NetclawConfigPath);
        Assert.False(config.RootElement.TryGetProperty("Models", out _));
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
    public async Task Discover_SuccessWithProviderWarning_PrintsWarning()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-openai"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai"
                }
            }
        });
        _fakeProbe.NextResult = new ProviderProbeResult(
            true,
            "provider returned fallback list",
            [new DiscoveredModel { ModelId = new Netclaw.Configuration.ModelId("gpt-5.3-codex") }]);

        var exitCode = await ModelCommand.RunAsync(
            ["model", "discover", "my-openai"],
            _paths, _fakeProbe, output: _output);

        Assert.Equal(0, exitCode);
        Assert.Contains("Warning: provider returned fallback list", _output.ToString());
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
        var roles = models.GetProperty("Roles");
        Assert.True(roles.TryGetProperty("Main", out _)); // Main still exists
        Assert.False(roles.TryGetProperty("Fallback", out _)); // Fallback removed
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
        var roles = models.GetProperty("Roles");
        Assert.True(roles.TryGetProperty("Main", out _)); // Main preserved
        var fallback = ReadActiveModel(config, "Fallback");
        Assert.Equal("qwen3:8b", fallback.GetProperty("ModelId").GetString());
    }

    [Fact]
    public async Task Set_InputModalities_WritesOverrideWithoutProbing()
    {
        WriteConfig(ProvidersOnly());

        // A non-OAuth provider never probes; --input-modalities is the manual override channel.
        var exitCode = await ModelCommand.RunAsync(
            ["model", "set", "main", "my-ollama", "qwen3:30b", "--input-modalities", "Text, Image"],
            _paths, output: _output);

        Assert.Equal(0, exitCode);
        using var config = ReadConfigFile(_paths.NetclawConfigPath);
        var main = ReadActiveModel(config, "Main");
        Assert.Equal("Text, Image", main.GetProperty("InputModalities").GetString());
    }

    [Fact]
    public async Task Set_ClearModalities_RemovesExistingOverride()
    {
        WriteConfig(WithMainModalities("Text, Image"));

        var exitCode = await ModelCommand.RunAsync(
            ["model", "set", "main", "my-ollama", "qwen3:30b", "--clear-modalities"],
            _paths, output: _output);

        Assert.Equal(0, exitCode);
        using var config = ReadConfigFile(_paths.NetclawConfigPath);
        var main = ReadActiveModel(config, "Main");
        Assert.False(main.TryGetProperty("InputModalities", out _)); // cleared → runtime detection
    }

    [Theory]
    [InlineData("invalid modalities", "--input-modalities", "Vision")]
    [InlineData("--input-modalities requires a value", "--input-modalities")]
    [InlineData("unknown argument '--input-modalites'", "--input-modalites", "Text")]
    [InlineData("invalid modalities", "--input-modalities", "3")]
    [InlineData("cannot be combined", "--context-window", "32768", "--clear-context-window")]
    public async Task Set_InvalidOptions_ReturnErrorWithoutWriting(
        string expectedError,
        params string[] options)
    {
        WriteConfig(ProvidersOnly());

        var exitCode = await ModelCommand.RunAsync(
            ["model", "set", "main", "my-ollama", "qwen3:30b", .. options],
            _paths, output: _output);

        Assert.Equal(1, exitCode);
        Assert.Contains(expectedError, _output.ToString());
        Assert.False(ReadConfigFile(_paths.NetclawConfigPath).RootElement.TryGetProperty("Models", out _));
    }

    [Fact]
    public async Task Set_SameModelReSetWithContextWindow_PreservesExistingModalities()
    {
        WriteConfig(WithMainModalities("Text, Image"));

        // End-to-end: a --context-window-only re-set of the same model must keep the operator's
        // modality override (#1127 / #5) — discovery/rebuild no longer wipes it.
        var exitCode = await ModelCommand.RunAsync(
            ["model", "set", "main", "my-ollama", "qwen3:30b", "--context-window", "65536"],
            _paths, output: _output);

        Assert.Equal(0, exitCode);
        using var config = ReadConfigFile(_paths.NetclawConfigPath);
        var main = ReadActiveModel(config, "Main");
        Assert.Equal("Text, Image", main.GetProperty("InputModalities").GetString());
        Assert.Equal(65536, main.GetProperty("ContextWindow").GetInt32());
    }

    [Fact]
    public async Task Set_ClearContextWindow_RemovesStoredClamp()
    {
        var config = ProvidersOnly();
        config["Models"] = new Dictionary<string, object>
        {
            ["Main"] = new Dictionary<string, object>
            {
                ["Provider"] = "my-ollama",
                ["ModelId"] = "qwen3:30b",
                ["ContextWindow"] = 32768
            }
        };
        WriteConfig(config);

        var exitCode = await ModelCommand.RunAsync(
            ["model", "set", "main", "my-ollama", "qwen3:30b", "--clear-context-window"],
            _paths, output: _output);

        Assert.Equal(0, exitCode);
        using var written = ReadConfigFile(_paths.NetclawConfigPath);
        var main = ReadActiveModel(written, "Main");
        Assert.False(main.TryGetProperty("ContextWindow", out _)); // clamp removed → runtime detection
    }

    [Fact]
    public async Task Set_OAuthModelWithModalityOverride_StillProbesAndValidates()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["openai-codex"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai",
                    ["AuthMethod"] = "OAuthDevice"
                }
            }
        });
        _fakeProbe.NextResult = new ProviderProbeResult(true, null,
        [
            new DiscoveredModel
            {
                ModelId = new Netclaw.Configuration.ModelId("gpt-new-codex"),
                ContextWindowTokens = 512000,
                InputModalities = ModelModality.Text | ModelModality.Image,
                OutputModalities = ModelModality.Text,
            }
        ]);

        // A modality override no longer short-circuits the probe: the probe must still run to
        // validate the model and discover the context window, while the operator's modality wins.
        var exitCode = await ModelCommand.RunAsync(
            ["model", "set", "main", "openai-codex", "gpt-new-codex", "--input-modalities", "Text"],
            _paths, _fakeProbe, output: _output);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, _fakeProbe.ProbeCallCount);            // probe ran despite the modality flag
        using var config = ReadConfigFile(_paths.NetclawConfigPath);
        var main = ReadActiveModel(config, "Main");
        Assert.Equal("Live", main.GetProperty("Provenance").GetString());     // resolved via probe
        Assert.Equal(512000, main.GetProperty("ContextWindow").GetInt32());   // discovered window captured
        Assert.Equal("Text", main.GetProperty("InputModalities").GetString());// operator override wins
    }

    [Fact]
    public async Task Set_OAuthModelWithModalityOverride_WhenModelNotReturned_ReturnsError()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["openai-codex"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai",
                    ["AuthMethod"] = "OAuthDevice"
                }
            }
        });
        _fakeProbe.NextResult = new ProviderProbeResult(true, null,
        [
            new DiscoveredModel { ModelId = new Netclaw.Configuration.ModelId("gpt-other-codex") }
        ]);

        // The modality flag must not let an unvalidated model slip through: the probe reports a
        // different model, so the set must fail rather than write an unverified entry (#1610).
        var exitCode = await ModelCommand.RunAsync(
            ["model", "set", "main", "openai-codex", "gpt-new-codex", "--input-modalities", "Text"],
            _paths, _fakeProbe, output: _output);

        Assert.Equal(1, exitCode);
        Assert.Contains("was not returned", _output.ToString());
        Assert.False(ReadConfigFile(_paths.NetclawConfigPath).RootElement.TryGetProperty("Models", out _));
    }

    [Fact]
    public async Task List_CorruptModalityConfig_ReturnsErrorWithoutCrashing()
    {
        // A config with an unparseable modality enum string must not crash `model list` with an
        // unhandled JsonException, nor be silently reported as "no models configured" (#1610).
        WriteConfig(WithMainEntry(new Dictionary<string, object>
        {
            ["Provider"] = "my-ollama",
            ["ModelId"] = "qwen3:30b",
            ["ContextWindow"] = 32768,
            ["InputModalities"] = "text_and_image"
        }));

        var exitCode = await ModelCommand.RunAsync(["model", "list"], _paths, output: _output);

        Assert.Equal(1, exitCode);
        Assert.Contains("could not be parsed", _output.ToString());
    }

    [Fact]
    public async Task Set_CorruptModalityButValidWindow_PreservesWindowEndToEnd()
    {
        // End-to-end regression for the full `model set` path: a re-set over a corrupt entry that
        // has a VALID ContextWindow must succeed and keep the window (repairing the bad modality),
        // not crash in the downgrade-check load path before reaching the writer (#1610).
        WriteConfig(WithMainEntry(new Dictionary<string, object>
        {
            ["Provider"] = "my-ollama",
            ["ModelId"] = "qwen3:30b",
            ["ContextWindow"] = 32768,
            ["InputModalities"] = "text_and_image"
        }));

        var exitCode = await ModelCommand.RunAsync(
            ["model", "set", "main", "my-ollama", "qwen3:30b"], _paths, output: _output);

        Assert.Equal(0, exitCode);
        using var config = ReadConfigFile(_paths.NetclawConfigPath);
        var main = ReadActiveModel(config, "Main");
        Assert.Equal(32768, main.GetProperty("ContextWindow").GetInt32()); // valid clamp preserved
        Assert.False(main.TryGetProperty("InputModalities", out _));        // corrupt override dropped
    }

    private static Dictionary<string, object> WithMainEntry(Dictionary<string, object> main)
    {
        var config = ProvidersOnly();
        config["Models"] = new Dictionary<string, object> { ["Main"] = main };
        return config;
    }

    private static JsonElement ReadActiveModel(JsonDocument config, string role)
    {
        var models = config.RootElement.GetProperty("Models");
        var definitionName = models.GetProperty("Roles").GetProperty(role).GetString()!;
        return models.GetProperty("Definitions").GetProperty(definitionName);
    }

    private static Dictionary<string, object> ProvidersOnly() => new()
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
    };

    private static Dictionary<string, object> WithMainModalities(string inputModalities)
    {
        var config = ProvidersOnly();
        config["Models"] = new Dictionary<string, object>
        {
            ["Main"] = new Dictionary<string, object>
            {
                ["Provider"] = "my-ollama",
                ["ModelId"] = "qwen3:30b",
                ["InputModalities"] = inputModalities
            }
        };
        return config;
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
