// -----------------------------------------------------------------------
// <copyright file="ModelCommandTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Model;
using Netclaw.Cli.Tests.Tui;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Model;

[Collection(Netclaw.Cli.Tests.LegacyModelEnvironmentCollection.Name)]
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
    public async Task Set_OpenAiOAuthModel_DoesNotPersistDiscoveredCapabilities()
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
        Assert.False(main.TryGetProperty("ContextWindow", out _));
        Assert.False(main.TryGetProperty("InputModalities", out _));
        Assert.False(main.TryGetProperty("OutputModalities", out _));
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
    public async Task Discover_StoredEndpointWithoutScheme_FailsInOneLineWithoutProbing()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["noscheme"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai-compatible",
                    ["Endpoint"] = "localhost:8080"
                }
            }
        });

        var exitCode = await ModelCommand.RunAsync(
            ["model", "discover", "noscheme"],
            _paths, _fakeProbe, output: _output);

        Assert.Equal(1, exitCode);
        Assert.Equal(
            "Error: Provider 'noscheme' has endpoint 'localhost:8080', which is not an absolute http or https URL. "
            + "Fix it by editing the provider's endpoint in `netclaw provider`.",
            _output.ToString().Trim());
        Assert.Equal(0, _fakeProbe.ProbeCallCount);
    }

    [Fact]
    public async Task Discover_StoredEndpointWithoutScheme_HintCannotDropTheStoredApiKey()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["keyed"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai-compatible",
                    ["Endpoint"] = "localhost:8080",
                    ["AuthMethod"] = "ApiKey"
                }
            }
        });
        File.WriteAllText(_paths.SecretsPath, """{"Providers":{"keyed":{"ApiKey":"sk-keep"}}}""");

        await ModelCommand.RunAsync(["model", "discover", "keyed"], _paths, _fakeProbe, output: _output);

        // `provider add` without --api-key rewrites the entry as unauthenticated and deletes the secret.
        Assert.DoesNotContain("provider add", _output.ToString(), StringComparison.Ordinal);
        Assert.Contains("sk-keep", File.ReadAllText(_paths.SecretsPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discover_ProviderNameInOtherCase_ProbesTheConfiguredProvider()
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
            ["model", "discover", "MY-Ollama"],
            _paths, _fakeProbe, output: _output);

        Assert.Equal(0, exitCode);
        Assert.Contains("Discovering models from 'my-ollama'", _output.ToString());
        Assert.Equal(1, _fakeProbe.ProbeCallCount);
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
    [InlineData("invalid modalities", "--input-modalities", "1")]
    [InlineData("invalid modalities", "--input-modalities", "2")]
    [InlineData("invalid modalities", "--input-modalities", "4")]
    [InlineData("invalid modalities", "--input-modalities", "8")]
    [InlineData("invalid modalities", "--output-modalities", "1")]
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

    [Theory]
    [InlineData("ContextWindow", "65536")]
    [InlineData("InputModalities", "Text, Image")]
    [InlineData("OutputModalities", "Text, Audio")]
    public async Task Set_SameModelWithoutCapabilityOptions_PreservesStoredCapability(
        string propertyName,
        string expectedValue)
    {
        WriteConfig(WithMainEntry(new Dictionary<string, object>
        {
            ["Provider"] = "my-ollama",
            ["ModelId"] = "qwen3:30b",
            ["ContextWindow"] = 65536,
            ["InputModalities"] = "Text, Image",
            ["OutputModalities"] = "Text, Audio"
        }));

        var exitCode = await ModelCommand.RunAsync(
            ["model", "set", "main", "my-ollama", "qwen3:30b"],
            _paths, output: _output);

        Assert.Equal(0, exitCode);
        using var config = ReadConfigFile(_paths.NetclawConfigPath);
        var main = ReadActiveModel(config, "Main");
        Assert.Equal(expectedValue, main.GetProperty(propertyName).ToString());
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

        // The probe still validates the model. The operator's modality remains authoritative.
        var exitCode = await ModelCommand.RunAsync(
            ["model", "set", "main", "openai-codex", "gpt-new-codex", "--input-modalities", "Text"],
            _paths, _fakeProbe, output: _output);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, _fakeProbe.ProbeCallCount);            // probe ran despite the modality flag
        using var config = ReadConfigFile(_paths.NetclawConfigPath);
        var main = ReadActiveModel(config, "Main");
        Assert.Equal("Live", main.GetProperty("Provenance").GetString());
        Assert.False(main.TryGetProperty("ContextWindow", out _));
        Assert.Equal("Text", main.GetProperty("InputModalities").GetString());
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
        Assert.Contains("Models:Main:InputModalities", _output.ToString());
        Assert.Contains("text_and_image", _output.ToString());
    }

    [Fact]
    public async Task List_MissingNamedDefinition_SurfacesResolverErrorWithoutAutofixGuidance()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Models"] = new Dictionary<string, object>
            {
                ["Definitions"] = new Dictionary<string, object>
                {
                    ["known"] = new Dictionary<string, object>
                    {
                        ["Provider"] = "my-ollama",
                        ["ModelId"] = "qwen3:30b"
                    }
                },
                ["Roles"] = new Dictionary<string, object>
                {
                    ["Main"] = "missing"
                }
            }
        });

        var exitCode = await ModelCommand.RunAsync(["model", "list"], _paths, output: _output);

        var output = _output.ToString();
        Assert.Equal(1, exitCode);
        Assert.Contains("Models:Roles:Main references unknown definition 'missing'.", output);
        Assert.Contains("Fix the Models section", output);
        Assert.DoesNotContain("could not be parsed", output);
        Assert.DoesNotContain("doctor --fix", output);
    }

    [Fact]
    public async Task List_NamedRolesWithLegacyEnvironmentRoles_ReportsTheMixTheDaemonRefuses()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Models"] = new Dictionary<string, object>
            {
                ["Definitions"] = new Dictionary<string, object>
                {
                    ["known"] = new Dictionary<string, object> { ["Provider"] = "my-ollama", ["ModelId"] = "qwen3:30b" }
                },
                ["Roles"] = new Dictionary<string, object> { ["Main"] = "known" }
            }
        });

        var exitCode = await WithEnvironment(
            [("NETCLAW_Models__Main__Provider", "stub"), ("NETCLAW_Models__Main__ModelId", "zzz")],
            () => ModelCommand.RunAsync(["model", "list"], _paths, output: _output));

        var output = _output.ToString();
        Assert.Equal(1, exitCode);
        Assert.Contains("Models configuration mixes legacy inline roles", output);
        Assert.DoesNotContain("qwen3:30b", output);
    }

    [Fact]
    public async Task List_RoleOverriddenByEnvironment_ShowsTheModelTheDaemonUses()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Models"] = new Dictionary<string, object>
            {
                ["Definitions"] = new Dictionary<string, object>
                {
                    ["known"] = new Dictionary<string, object> { ["Provider"] = "my-ollama", ["ModelId"] = "qwen3:30b" }
                },
                ["Roles"] = new Dictionary<string, object> { ["Main"] = "known" }
            }
        });

        var exitCode = await WithEnvironment(
            [("NETCLAW_Models__Definitions__known__ModelId", "from-env")],
            () => ModelCommand.RunAsync(["model", "list"], _paths, output: _output));

        Assert.Equal(0, exitCode);
        Assert.Contains("from-env", _output.ToString());
        Assert.DoesNotContain("qwen3:30b", _output.ToString());
        Assert.Contains("NETCLAW_Models__Definitions__known__ModelId", _output.ToString());
    }

    [Fact]
    public async Task ProviderRemove_GuardReadsTheFileNotTheShellEnvironment()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-ollama"] = new Dictionary<string, object> { ["Type"] = "ollama", ["Endpoint"] = "http://localhost:11434" },
                ["other"] = new Dictionary<string, object> { ["Type"] = "ollama", ["Endpoint"] = "http://localhost:11435" }
            },
            ["Models"] = new Dictionary<string, object>
            {
                ["Definitions"] = new Dictionary<string, object>
                {
                    ["known"] = new Dictionary<string, object> { ["Provider"] = "my-ollama", ["ModelId"] = "qwen3:30b" }
                },
                ["Roles"] = new Dictionary<string, object> { ["Main"] = "known" }
            }
        });

        var exitCode = await WithEnvironment(
            [("NETCLAW_Models__Definitions__known__Provider", "other")],
            () => Netclaw.Cli.Provider.ProviderCommand.RunAsync(["provider", "remove", "my-ollama"], _paths, output: _output));

        Assert.Equal(1, exitCode);
        Assert.Contains("referenced by model role(s): Main", _output.ToString());
    }

    [Fact]
    public async Task Set_ProviderNameInOtherCase_WritesTheConfiguredSpelling()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-ollama"] = new Dictionary<string, object> { ["Type"] = "ollama", ["Endpoint"] = "http://localhost:11434" }
            }
        });

        var exitCode = await ModelCommand.RunAsync(
            ["model", "set", "main", "MY-Ollama", "qwen3:30b", "--context-window", "32768"], _paths, output: _output);

        Assert.Equal(0, exitCode);
        Assert.Contains("Set main model to my-ollama/qwen3:30b", _output.ToString());
    }

    private static async Task<int> WithEnvironment((string Name, string Value)[] variables, Func<Task<int>> run)
    {
        var previous = variables.Select(v => Environment.GetEnvironmentVariable(v.Name)).ToArray();
        try
        {
            foreach (var (name, value) in variables)
                Environment.SetEnvironmentVariable(name, value);

            return await run();
        }
        finally
        {
            for (var i = 0; i < variables.Length; i++)
                Environment.SetEnvironmentVariable(variables[i].Name, previous[i]);
        }
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

    [Fact]
    public async Task Set_LegacyEnvironmentOverride_ReturnsErrorWithoutChangingConfig()
    {
        var config = ProvidersOnly();
        config["Models"] = new Dictionary<string, object>
        {
            ["Main"] = new Dictionary<string, object>
            {
                ["Provider"] = "my-ollama",
                ["ModelId"] = "qwen3:30b"
            }
        };
        WriteConfig(config);
        var original = File.ReadAllText(_paths.NetclawConfigPath);
        const string envVar = "NETCLAW_Models__Main__ContextWindow";
        var previous = Environment.GetEnvironmentVariable(envVar);

        try
        {
            Environment.SetEnvironmentVariable(envVar, "65536");

            var exitCode = await ModelCommand.RunAsync(
                ["model", "set", "main", "my-ollama", "qwen3:8b"], _paths, output: _output);

            Assert.Equal(1, exitCode);
            Assert.Contains(
                $"Error: Cannot migrate Models while legacy environment override '{envVar}' is set.",
                _output.ToString(), StringComparison.Ordinal);
            Assert.Equal(original, File.ReadAllText(_paths.NetclawConfigPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, previous);
        }
    }

    [Fact]
    public async Task Set_ConflictingLegacyRoles_ReturnsErrorWithoutChangingConfig()
    {
        var config = ProvidersOnly();
        config["Models"] = new Dictionary<string, object>
        {
            ["Main"] = new Dictionary<string, object>
            {
                ["Provider"] = "my-ollama",
                ["ModelId"] = "qwen3:30b",
                ["ContextWindow"] = 32768
            },
            ["Fallback"] = new Dictionary<string, object>
            {
                ["Provider"] = "my-ollama",
                ["ModelId"] = "qwen3:30b",
                ["ContextWindow"] = 65536
            }
        };
        WriteConfig(config);
        var original = File.ReadAllText(_paths.NetclawConfigPath);

        var exitCode = await ModelCommand.RunAsync(
            ["model", "set", "compaction", "my-ollama", "qwen3:30b"], _paths, output: _output);

        Assert.Equal(1, exitCode);
        Assert.Contains(
            "Error: Legacy model roles conflict for my-ollama/qwen3:30b; align their metadata before migration.",
            _output.ToString(), StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public async Task Clear_LegacyEnvironmentOverride_ReturnsErrorWithoutChangingConfig()
    {
        var config = ProvidersOnly();
        config["Models"] = new Dictionary<string, object>
        {
            ["Main"] = new Dictionary<string, object>
            {
                ["Provider"] = "my-ollama",
                ["ModelId"] = "qwen3:30b"
            },
            ["Fallback"] = new Dictionary<string, object>
            {
                ["Provider"] = "my-ollama",
                ["ModelId"] = "qwen3:8b"
            }
        };
        WriteConfig(config);
        var original = File.ReadAllText(_paths.NetclawConfigPath);
        const string envVar = "NETCLAW_Models__Fallback__ContextWindow";
        var previous = Environment.GetEnvironmentVariable(envVar);

        try
        {
            Environment.SetEnvironmentVariable(envVar, "65536");

            var exitCode = await ModelCommand.RunAsync(
                ["model", "clear", "fallback"], _paths, output: _output);

            Assert.Equal(1, exitCode);
            Assert.Contains(
                $"Error: Cannot migrate Models while legacy environment override '{envVar}' is set.",
                _output.ToString(), StringComparison.Ordinal);
            Assert.Equal(original, File.ReadAllText(_paths.NetclawConfigPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, previous);
        }
    }

    private const string Ollama = """{"my-ollama":{"Type":"ollama","Endpoint":"http://localhost:11434"}}""";

    [Theory]
    [InlineData("""{"Definitions":{"d":{"Provider":"my-ollama","ModelId":"m1"}},"Roles":{"Main":"nope"}}""", "main")]
    [InlineData("""{"Definitions":{"d":{"Provider":"my-ollama","ModelId":"m1"}},"Roles":{"Main":"d","Compaction":"nope"}}""", "compaction")]
    [InlineData("""{"Definitions":{"d":{"Provider":"my-ollama","ModelId":"m1"}},"Roles":{"Main":"d","Fallback":"nope"}}""", "fallback")]
    [InlineData("""{"Definitions":{"d":{"Provider":"my-ollama","ModelId":"m1"}},"Roles":{"Fallback":"d"}}""", "main")]
    [InlineData("""{"Definitions":{},"Roles":{"Main":"d"}}""", "main")]
    [InlineData("""{"Definitions":{"d":{"Provider":"my-ollama","ModelId":"m1"}},"Roles":{}}""", "main")]
    public async Task Set_RepairsTheShapeWhoseResolverMessageAdvertisesIt(string modelsJson, string role)
    {
        File.WriteAllText(_paths.NetclawConfigPath, $$"""{"configVersion":1,"Providers":{{Ollama}},"Models":{{modelsJson}}}""");
        Assert.False(ModelCommand.TryLoadModelSelection(_paths, out _, out var error));
        Assert.Contains($"`netclaw model set {role} <provider> <model>`", error);

        var exitCode = await ModelCommand.RunAsync(
            ["model", "set", role, "my-ollama", "qwen3:30b"], _paths, output: _output);

        Assert.Equal(0, exitCode);
        Assert.True(ModelCommand.TryLoadModelSelection(_paths, out var models, out _));
        Assert.Equal("qwen3:30b", role == "main" ? models!.Main.ModelId : (role == "fallback" ? models!.Fallback! : models!.Compaction!).ModelId);
    }

    [Theory]
    [InlineData("""{"Main":{"Provider":"my-ollama","ModelId":"m1"},"Definitions":{"d":{"Provider":"my-ollama","ModelId":"m2"}},"Roles":{"Main":"d"}}""")]
    [InlineData("""{"Definitions":{"d":{"Provider":"my-ollama","ModelId":"m1"}}}""")]
    [InlineData("""{"Roles":{"Main":"d"}}""")]
    public async Task Set_DoesNotRepairTheShapesThatNeedAHandEdit(string modelsJson)
    {
        File.WriteAllText(_paths.NetclawConfigPath, $$"""{"configVersion":1,"Providers":{{Ollama}},"Models":{{modelsJson}}}""");
        var original = File.ReadAllText(_paths.NetclawConfigPath);
        Assert.False(ModelCommand.TryLoadModelSelection(_paths, out _, out _));

        var exitCode = await ModelCommand.RunAsync(
            ["model", "set", "main", "my-ollama", "qwen3:30b"], _paths, output: _output);

        Assert.Equal(1, exitCode);
        Assert.Equal(original, File.ReadAllText(_paths.NetclawConfigPath));
    }

    // ── Output limit and thinking level (model-output-and-thinking-settings, tasks 2.10) ──
    //
    // A reject row must exit non-zero, print its reason, and leave the netclaw.json bytes unchanged.
    // A save row must exit zero, and a fresh load through ModelConfigurationResolver must give the
    // values that the daemon reads at startup.

    private sealed record SettingsRow(
        string ProviderType,
        string ModelId,
        string[] Flags,
        ProviderProbeResult Probe,
        Dictionary<string, object>? VendorOptions = null,
        Dictionary<string, object>? SeededMain = null,
        string? RejectPattern = null,
        Action<ModelReference, JsonDocument>? AssertSaved = null);

    private const string SettingsProvider = "prov";

    private static ProviderProbeResult ProbeOf(
        string modelId,
        ThinkingSupport? thinking = null,
        int? maxOutputTokens = null,
        ThinkingDialect? detectedDialect = null)
        => new(true, null,
        [
            new DiscoveredModel
            {
                ModelId = new Netclaw.Configuration.ModelId(modelId),
                Thinking = thinking ?? ThinkingSupport.Unknown,
                MaxOutputTokens = maxOutputTokens,
            }
        ])
        { DetectedThinkingDialect = detectedDialect };

    private static readonly ThinkingSupport AllLevels = ThinkingSupport.Controllable(
        ThinkingLevel.Off, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.Max);

    private static readonly Dictionary<string, object> ReasoningEffortDialect = new() { ["ThinkingDialect"] = "ReasoningEffort" };

    private static readonly Dictionary<string, SettingsRow> SettingsRows = new()
    {
        ["set_saves_limit_and_thinking_and_reload_resolves_them"] = new(
            "openai", "gpt-5", ["--max-output-tokens", "32000", "--thinking", "low"],
            ProbeOf("gpt-5", ThinkingSupport.Controllable(ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High)),
            AssertSaved: (main, file) =>
            {
                Assert.Equal(32000, main.MaxOutputTokens);
                Assert.Equal(ThinkingLevel.Low, main.Thinking);
                var definition = ReadActiveModel(file, "Main");
                Assert.Equal(32000, definition.GetProperty("MaxOutputTokens").GetInt32());
                Assert.Equal("Low", definition.GetProperty("Thinking").GetString());
            }),
        ["zero_limit_rejected_file_unchanged"] = new(
            "openai", "gpt-5", ["--max-output-tokens", "0"], ProbeOf("gpt-5"),
            RejectPattern: Regex.Escape("--max-output-tokens must be a positive integer")),
        ["limit_not_below_context_window_rejected_file_unchanged"] = new(
            "openai", "gpt-5", ["--max-output-tokens", "32768"], ProbeOf("gpt-5"),
            SeededMain: new() { ["Provider"] = SettingsProvider, ["ModelId"] = "gpt-5", ["ContextWindow"] = 32768 },
            RejectPattern: "(?i)context window"),
        ["limit_above_known_model_max_rejected_file_unchanged"] = new(
            "openai", "gpt-5", ["--max-output-tokens", "32000"], ProbeOf("gpt-5", maxOutputTokens: 16384),
            RejectPattern: "16,?384"),
        ["deepseek_medium_rejected_lists_offered_levels_file_unchanged"] = new(
            "deepseek", "deepseek-v4", ["--thinking", "medium"], ProbeOf("deepseek-v4", AllLevels),
            RejectPattern: Regex.Escape("off, low, high, max")),
        ["unknown_capability_low_rejected_file_unchanged"] = new(
            "openai-compatible", "qwen3", ["--thinking", "low"], ProbeOf("qwen3"),
            VendorOptions: ReasoningEffortDialect,
            RejectPattern: Regex.Escape("thinking support unknown for this endpoint")),
        ["unknown_capability_off_saved"] = new(
            "openai-compatible", "qwen3", ["--thinking", "off"], ProbeOf("qwen3"),
            VendorOptions: ReasoningEffortDialect,
            AssertSaved: (main, _) => Assert.Equal(ThinkingLevel.Off, main.Thinking)),
        ["vllm_without_dialect_thinking_rejected_names_key_file_unchanged"] = new(
            "openai-compatible", "qwen3", ["--thinking", "off"], ProbeOf("qwen3"),
            RejectPattern: "(?s)(?=.*ThinkingDialect)(?=.*ChatTemplateKwargs)(?=.*ReasoningEffort)(?=.*DeepSeek)(?=.*None)"),
        ["vllm_without_dialect_limit_saved"] = new(
            "openai-compatible", "qwen3", ["--max-output-tokens", "16000"], ProbeOf("qwen3"),
            AssertSaved: (main, _) =>
            {
                Assert.Equal(16000, main.MaxOutputTokens);
                Assert.Null(main.Thinking);
            }),
        ["llama_cpp_detected_dialect_saved_with_definition"] = new(
            "openai-compatible", "qwen3", ["--thinking", "off"],
            ProbeOf("qwen3", detectedDialect: ThinkingDialect.ReasoningEffort),
            AssertSaved: (main, file) =>
            {
                Assert.Equal(ThinkingLevel.Off, main.Thinking);
                Assert.Equal("ReasoningEffort", file.RootElement.GetProperty("Providers").GetProperty(SettingsProvider)
                    .GetProperty("VendorOptions").GetProperty("ThinkingDialect").GetString());
            }),
        ["ollama_disable_thinking_conflict_rejected_file_unchanged"] = new(
            "ollama", "qwen3:30b", ["--thinking", "low"],
            ProbeOf("qwen3:30b", ThinkingSupport.Controllable(ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High)),
            VendorOptions: new() { ["DisableThinking"] = true },
            RejectPattern: "DisableThinking"),
        ["clear_flags_remove_settings"] = new(
            "openai", "gpt-5", ["--clear-max-output-tokens", "--clear-thinking"], ProbeOf("gpt-5", AllLevels),
            SeededMain: new() { ["Provider"] = SettingsProvider, ["ModelId"] = "gpt-5", ["MaxOutputTokens"] = 32000, ["Thinking"] = "Low" },
            AssertSaved: (main, file) =>
            {
                Assert.Null(main.MaxOutputTokens);
                Assert.Null(main.Thinking);
                var definition = ReadActiveModel(file, "Main");
                Assert.False(definition.TryGetProperty("MaxOutputTokens", out _));
                Assert.False(definition.TryGetProperty("Thinking", out _));
            }),
        // A context window alone runs the whole validator: the stored limit must stay below it.
        ["context_window_below_stored_limit_rejected_file_unchanged"] = new(
            "openai", "gpt-5", ["--context-window", "16384"], ProbeOf("gpt-5"),
            SeededMain: new() { ["Provider"] = SettingsProvider, ["ModelId"] = "gpt-5", ["MaxOutputTokens"] = 32000 },
            RejectPattern: "(?is)(context window.*32,?000|32,?000.*context window)"),
        // The output limit needs no capability, so a failed probe does not block it.
        ["limit_only_saved_when_probe_fails"] = new(
            "openai-compatible", "qwen3", ["--max-output-tokens", "16000"],
            new ProviderProbeResult(false, "connection refused", []),
            AssertSaved: (main, _) => Assert.Equal(16000, main.MaxOutputTokens)),
        ["set_and_clear_together_rejected"] = new(
            "openai", "gpt-5", ["--thinking", "low", "--clear-thinking"], ProbeOf("gpt-5", AllLevels),
            RejectPattern: "cannot be combined"),
    };

    public static TheoryData<string> SettingsRowNames => new(SettingsRows.Keys);

    [Theory]
    [MemberData(nameof(SettingsRowNames))]
    public async Task Set_validates_output_limit_and_thinking_before_persistence(string row)
    {
        var spec = SettingsRows[row];
        var provider = new Dictionary<string, object> { ["Type"] = spec.ProviderType };
        if (spec.ProviderType is "openai-compatible" or "ollama")
            provider["Endpoint"] = "http://localhost:8080";
        if (spec.VendorOptions is not null)
            provider["VendorOptions"] = spec.VendorOptions;
        var config = new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object> { [SettingsProvider] = provider },
        };
        if (spec.SeededMain is not null)
        {
            config["Models"] = new Dictionary<string, object>
            {
                ["Definitions"] = new Dictionary<string, object> { ["seeded"] = spec.SeededMain },
                ["Roles"] = new Dictionary<string, object> { ["Main"] = "seeded" },
            };
        }

        WriteConfig(config);
        if (spec.ProviderType is "openai" or "deepseek")
        {
            WriteSecrets(new Dictionary<string, object>
            {
                ["Providers"] = new Dictionary<string, object>
                {
                    [SettingsProvider] = new Dictionary<string, object> { ["ApiKey"] = "test-key" },
                },
            });
        }

        _fakeProbe.NextResult = spec.Probe;
        var before = File.ReadAllBytes(_paths.NetclawConfigPath);

        var exitCode = await ModelCommand.RunAsync(
            ["model", "set", "main", SettingsProvider, spec.ModelId, .. spec.Flags], _paths, _fakeProbe, _output);

        var output = _output.ToString();
        if (spec.RejectPattern is { } pattern)
        {
            Assert.NotEqual(0, exitCode);
            Assert.Matches(pattern, output);
            Assert.Equal(before, File.ReadAllBytes(_paths.NetclawConfigPath));
            return;
        }

        Assert.True(exitCode == 0, output);
        var reloaded = ModelConfigurationResolver.Resolve(new ConfigurationBuilder()
            .AddJsonFile(_paths.NetclawConfigPath, optional: false, reloadOnChange: false)
            .Build()).Selection;
        using var file = ReadConfigFile(_paths.NetclawConfigPath);
        spec.AssertSaved!(reloaded.Main, file);
    }

    // Two definitions of one model (sonnet-low and sonnet-high). model set edits the definition that
    // the role uses now, not the first definition of that model, and names the other roles of it.
    [Fact]
    public async Task set_edits_the_roles_current_definition_and_names_its_other_roles()
    {
        var config = ProvidersOnly();
        config["Models"] = new Dictionary<string, object>
        {
            ["Definitions"] = new Dictionary<string, object>
            {
                ["sonnet-low"] = new Dictionary<string, object> { ["Provider"] = "my-ollama", ["ModelId"] = "sonnet", ["Thinking"] = "Low" },
                ["sonnet-high"] = new Dictionary<string, object> { ["Provider"] = "my-ollama", ["ModelId"] = "sonnet", ["Thinking"] = "High" },
            },
            ["Roles"] = new Dictionary<string, object> { ["Main"] = "sonnet-high", ["Fallback"] = "sonnet-low", ["Compaction"] = "sonnet-high" },
        };
        WriteConfig(config);
        _fakeProbe.NextResult = ProbeOf("sonnet", AllLevels);

        var exitCode = await ModelCommand.RunAsync(
            ["model", "set", "main", "my-ollama", "sonnet", "--max-output-tokens", "16000"], _paths, _fakeProbe, _output);

        var output = _output.ToString();
        Assert.True(exitCode == 0, output);
        using var file = ReadConfigFile(_paths.NetclawConfigPath);
        var models = file.RootElement.GetProperty("Models");
        Assert.Equal("sonnet-high", models.GetProperty("Roles").GetProperty("Main").GetString());
        var high = models.GetProperty("Definitions").GetProperty("sonnet-high");
        Assert.Equal(16000, high.GetProperty("MaxOutputTokens").GetInt32());
        Assert.Equal("High", high.GetProperty("Thinking").GetString());
        Assert.False(models.GetProperty("Definitions").GetProperty("sonnet-low").TryGetProperty("MaxOutputTokens", out _));
        Assert.Matches("(?i)also used by:? *Compaction", output);
    }

    // A context-window change edits a shared definition, so it names the other roles too.
    [Fact]
    public async Task context_window_change_names_the_other_roles()
    {
        var config = ProvidersOnly();
        config["Models"] = new Dictionary<string, object>
        {
            ["Definitions"] = new Dictionary<string, object>
            {
                ["shared"] = new Dictionary<string, object> { ["Provider"] = "my-ollama", ["ModelId"] = "qwen3:30b" },
            },
            ["Roles"] = new Dictionary<string, object> { ["Main"] = "shared", ["Compaction"] = "shared" },
        };
        WriteConfig(config);
        _fakeProbe.NextResult = ProbeOf("qwen3:30b");

        var exitCode = await ModelCommand.RunAsync(
            ["model", "set", "main", "my-ollama", "qwen3:30b", "--context-window", "65536"], _paths, _fakeProbe, _output);

        var output = _output.ToString();
        Assert.True(exitCode == 0, output);
        Assert.Matches("(?i)also used by:? *Compaction", output);
    }

    [Fact]
    public async Task list_shows_max_out_and_thinking_columns()
    {
        var config = ProvidersOnly();
        config["Models"] = new Dictionary<string, object>
        {
            ["Definitions"] = new Dictionary<string, object>
            {
                ["main-def"] = new Dictionary<string, object>
                {
                    ["Provider"] = "my-ollama", ["ModelId"] = "qwen3:30b", ["MaxOutputTokens"] = 64000, ["Thinking"] = "Low",
                },
                ["compaction-def"] = new Dictionary<string, object> { ["Provider"] = "my-ollama", ["ModelId"] = "qwen3:8b" },
            },
            ["Roles"] = new Dictionary<string, object> { ["Main"] = "main-def", ["Compaction"] = "compaction-def" },
        };
        WriteConfig(config);

        Assert.Equal(0, await ModelCommand.RunAsync(["model", "list"], _paths, output: _output));

        var lines = _output.ToString().Split('\n');
        var header = lines.Single(line => line.Contains("Role", StringComparison.Ordinal));
        Assert.Contains("Max out", header, StringComparison.Ordinal);
        Assert.Contains("Thinking", header, StringComparison.Ordinal);
        var main = lines.Single(line => line.StartsWith("Main", StringComparison.Ordinal));
        Assert.Matches("64,?000", main);
        Assert.Contains("low", main, StringComparison.Ordinal);
        var compaction = lines.Single(line => line.StartsWith("Compaction", StringComparison.Ordinal));
        Assert.Contains("default 32000", compaction, StringComparison.Ordinal);
        Assert.Matches(@"default 32000.*\bdefault\b", compaction);
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
