// -----------------------------------------------------------------------
// <copyright file="ProviderCommandTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Cli.Provider;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Providers.OAuth;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Provider;

public sealed class ProviderCommandTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly StringWriter _output = new();

    public ProviderCommandTests()
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
    public async Task List_NoProviders_ShowsEmptyMessage()
    {
        await ProviderCommand.RunAsync(["provider", "list"], _paths, output: _output);

        Assert.Contains("No providers configured", _output.ToString());
    }

    [Fact]
    public async Task List_WithProviders_ShowsFormattedTable()
    {
        // Arrange: add a provider manually
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-ollama"] = new Dictionary<string, object>
                {
                    ["Type"] = "ollama",
                    ["Endpoint"] = "http://my-gpu-server:11434",
                    ["AuthMethod"] = "None"
                }
            }
        });

        await ProviderCommand.RunAsync(["provider", "list"], _paths, output: _output);
        var output = _output.ToString();

        Assert.Contains("my-ollama", output);
        Assert.Contains("ollama", output);
        Assert.Contains("http://my-gpu-server:11434", output);
    }

    [Fact]
    public async Task Add_OllamaProvider_WritesConfig()
    {
        var exitCode = await ProviderCommand.RunAsync(
            ["provider", "add", "my-ollama", "ollama", "--endpoint", "http://my-gpu-server:11434"],
            _paths, output: _output);

        Assert.Equal(0, exitCode);

        var config = ReadConfigFile(_paths.NetclawConfigPath);
        Assert.True(config.RootElement.TryGetProperty("Providers", out var providers));
        Assert.True(providers.TryGetProperty("my-ollama", out var entry));
        Assert.Equal("ollama", entry.GetProperty("Type").GetString());
        Assert.Equal("http://my-gpu-server:11434", entry.GetProperty("Endpoint").GetString());
    }

    [Fact]
    public async Task Add_ApiKeyProvider_WritesConfigAndSecrets()
    {
        var exitCode = await ProviderCommand.RunAsync(
            ["provider", "add", "my-openrouter", "openrouter", "--api-key", "sk-or-test-123"],
            _paths, output: _output);

        Assert.Equal(0, exitCode);

        // Verify config
        var config = ReadConfigFile(_paths.NetclawConfigPath);
        Assert.True(config.RootElement.TryGetProperty("Providers", out var providers));
        Assert.True(providers.TryGetProperty("my-openrouter", out var entry));
        Assert.Equal("openrouter", entry.GetProperty("Type").GetString());
        Assert.Equal("ApiKey", entry.GetProperty("AuthMethod").GetString());

        // Verify secrets
        var secrets = ReadConfigFile(_paths.SecretsPath);
        Assert.True(secrets.RootElement.TryGetProperty("Providers", out var secretProviders));
        Assert.True(secretProviders.TryGetProperty("my-openrouter", out var secretEntry));
        var encrypted = secretEntry.GetProperty("ApiKey").GetString();
        Assert.StartsWith("ENC:", encrypted);

        // Verify provider loader decrypts back to usable plaintext
        var loaded = ProviderCommand.LoadProviders(_paths);
        Assert.Equal("sk-or-test-123", loaded["my-openrouter"].ApiKey?.Value);
    }

    [Fact]
    public async Task Add_WithAuthApiKeyFlag_SucceedsForApiKeyProvider()
    {
        var exitCode = await ProviderCommand.RunAsync(
            ["provider", "add", "my-openai", "openai", "--auth", "api-key", "--api-key", "sk-openai-test-123"],
            _paths, output: _output);

        Assert.Equal(0, exitCode);

        var config = ReadConfigFile(_paths.NetclawConfigPath);
        var provider = config.RootElement.GetProperty("Providers").GetProperty("my-openai");
        Assert.Equal("ApiKey", provider.GetProperty("AuthMethod").GetString());
    }

    [Fact]
    public void ShouldDefaultToOAuthDevice_ReturnsTrueForOpenAiWithoutExplicitCredentialChoice()
    {
        var result = ProviderCommand.ShouldDefaultToOAuthDevice(
            "openai",
            apiKey: null,
            requestedAuthMethod: null,
            [AuthMethod.OAuthDevice, AuthMethod.OAuthPkce, AuthMethod.ApiKey]);

        Assert.True(result);
    }

    [Theory]
    [InlineData("openai", "sk-test", null)]
    [InlineData("openai", null, AuthMethod.ApiKey)]
    [InlineData("anthropic", null, null)]
    public void ShouldDefaultToOAuthDevice_ReturnsFalseWhenChoiceIsNotImplicitOpenAiOAuth(
        string providerType,
        string? apiKey,
        AuthMethod? requestedAuthMethod)
    {
        var result = ProviderCommand.ShouldDefaultToOAuthDevice(
            providerType,
            apiKey,
            requestedAuthMethod,
            [AuthMethod.OAuthDevice, AuthMethod.OAuthPkce, AuthMethod.ApiKey]);

        Assert.False(result);
    }

    [Fact]
    public async Task Add_WithUnknownAuthMethod_ReturnsError()
    {
        var exitCode = await ProviderCommand.RunAsync(
            ["provider", "add", "my-openai", "openai", "--auth", "bogus-auth"],
            _paths, output: _output);

        Assert.Equal(1, exitCode);
        Assert.Contains("Unknown auth method", _output.ToString());
    }

    [Fact]
    public async Task Add_InvalidType_ReturnsError()
    {
        var exitCode = await ProviderCommand.RunAsync(
            ["provider", "add", "my-provider", "unknown-type"],
            _paths, output: _output);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Add_OAuthOnlyProvider_WithoutAuthFlag_RefusesAndGuides()
    {
        // GitHub Copilot supports only OAuthDevice. Without --auth oauth-device
        // we must not silently write an entry with no credentials — fail loudly
        // and tell the operator how to authenticate.
        var exitCode = await ProviderCommand.RunAsync(
            ["provider", "add", "my-copilot", "github-copilot"],
            _paths, output: _output);

        Assert.Equal(1, exitCode);
        var output = _output.ToString();
        Assert.Contains("--auth oauth-device", output);
        Assert.Contains("requires OAuth device flow", output);

        // No partial entry written to netclaw.json
        Assert.False(File.Exists(_paths.NetclawConfigPath)
            && ReadConfigFile(_paths.NetclawConfigPath).RootElement
                .TryGetProperty("Providers", out var providers)
            && providers.TryGetProperty("my-copilot", out _));
    }

    [Fact]
    public async Task Remove_UnreferencedProvider_Succeeds()
    {
        // Arrange: add a provider
        await ProviderCommand.RunAsync(
            ["provider", "add", "my-ollama", "ollama", "--endpoint", "http://localhost:11434"],
            _paths, output: _output);

        // Act
        var exitCode = await ProviderCommand.RunAsync(
            ["provider", "remove", "my-ollama"],
            _paths, output: _output);

        Assert.Equal(0, exitCode);

        // Verify it's gone
        var providers = ProviderCommand.LoadProviders(_paths);
        Assert.Empty(providers);
    }

    [Fact]
    public async Task Remove_ReferencedProvider_ReturnsError()
    {
        // Arrange: add provider and assign it to main model role
        await ProviderCommand.RunAsync(
            ["provider", "add", "my-ollama", "ollama", "--endpoint", "http://localhost:11434"],
            _paths, output: _output);

        // Write a model reference pointing to this provider
        var config = JsonSerializer.Deserialize<Dictionary<string, object>>(
            File.ReadAllText(_paths.NetclawConfigPath))!;
        config["Models"] = new Dictionary<string, object>
        {
            ["Main"] = new Dictionary<string, object>
            {
                ["Provider"] = "my-ollama",
                ["ModelId"] = "qwen3:30b"
            }
        };
        File.WriteAllText(_paths.NetclawConfigPath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

        // Act
        var exitCode = await ProviderCommand.RunAsync(
            ["provider", "remove", "my-ollama"],
            _paths, output: _output);

        Assert.Equal(1, exitCode);
        var output = _output.ToString();
        Assert.Contains("Cannot remove", output);
        Assert.Contains("Main", output);
    }

    [Fact]
    public async Task Remove_NotFound_ReturnsError()
    {
        var exitCode = await ProviderCommand.RunAsync(
            ["provider", "remove", "nonexistent"],
            _paths, output: _output);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task LoadProviders_MergesConfigAndSecrets()
    {
        // Arrange: set up config and secrets separately
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-anthropic"] = new Dictionary<string, object>
                {
                    ["Type"] = "anthropic",
                    ["Endpoint"] = "https://api.anthropic.com",
                    ["AuthMethod"] = "ApiKey"
                }
            }
        });

        WriteSecrets(new Dictionary<string, object>
        {
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-anthropic"] = new Dictionary<string, object>
                {
                    ["ApiKey"] = "sk-ant-test"
                }
            }
        });

        var providers = ProviderCommand.LoadProviders(_paths);

        Assert.Single(providers);
        Assert.True(providers.ContainsKey("my-anthropic"));
        Assert.Equal("anthropic", providers["my-anthropic"].Type);
        Assert.Equal("sk-ant-test", providers["my-anthropic"].ApiKey?.Value);
    }

    [Fact]
    public void WriteProvider_OAuth_MergesMultipleProviders()
    {
        var registry = ProviderCommand.CreateDefaultRegistry();
        var protector = new NullSecretsProtector();

        ProviderCredentialWriter.WriteProvider(
            _paths,
            "openai-codex",
            "openai",
            AuthMethod.OAuthDevice,
            endpoint: null,
            oauthResult: new OAuthDeviceFlowResult(
                new SensitiveString("openai-access-token"),
                new SensitiveString("openai-refresh-token"),
                DateTimeOffset.UtcNow.AddHours(1),
                new SensitiveString("openai-account")),
            apiKey: null,
            registry,
            protector);

        ProviderCredentialWriter.WriteProvider(
            _paths,
            "my-copilot",
            "github-copilot",
            AuthMethod.OAuthDevice,
            endpoint: null,
            oauthResult: new OAuthDeviceFlowResult(
                new SensitiveString("copilot-access-token"),
                null,
                DateTimeOffset.UtcNow.AddHours(1),
                null),
            apiKey: null,
            registry,
            protector);

        using var config = ReadConfigFile(_paths.NetclawConfigPath);
        var configProviders = config.RootElement.GetProperty("Providers");
        Assert.True(configProviders.TryGetProperty("openai-codex", out _));
        Assert.True(configProviders.TryGetProperty("my-copilot", out _));

        using var secrets = ReadConfigFile(_paths.SecretsPath);
        var secretProviders = secrets.RootElement.GetProperty("Providers");
        Assert.Equal("openai-access-token",
            secretProviders.GetProperty("openai-codex").GetProperty("OAuthAccessToken").GetString());
        Assert.Equal("copilot-access-token",
            secretProviders.GetProperty("my-copilot").GetProperty("OAuthAccessToken").GetString());

        var loaded = ProviderCommand.LoadProviders(_paths);
        Assert.Equal("openai-access-token", loaded["openai-codex"].OAuthAccessToken?.Value);
        Assert.Equal("copilot-access-token", loaded["my-copilot"].OAuthAccessToken?.Value);
    }

    [Fact]
    public void LoadProviders_DecryptsEncryptedOAuthTokenExpiry()
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

        var protector = SecretsProtection.CreateProtector(_paths);
        var expiry = DateTimeOffset.Parse("2026-03-05T00:00:00+00:00").ToString("o");

        WriteSecrets(new Dictionary<string, object>
        {
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-openai"] = new Dictionary<string, object>
                {
                    ["OAuthAccessToken"] = protector.Protect("oauth-access-token"),
                    ["OAuthAccountId"] = protector.Protect("account-123"),
                    ["OAuthTokenExpiry"] = protector.Protect(expiry)
                }
            }
        });

        var providers = ProviderCommand.LoadProviders(_paths);

        Assert.True(providers.ContainsKey("my-openai"));
        Assert.Equal("account-123", providers["my-openai"].OAuthAccountId?.Value);
        Assert.NotNull(providers["my-openai"].OAuthTokenExpiry);
        Assert.Equal(DateTimeOffset.Parse(expiry), providers["my-openai"].OAuthTokenExpiry!.Value);
    }

    [Fact]
    public async Task Rename_CascadesToModelRoles()
    {
        // End-to-end testing surfaced that the old "config-key-only" behavior
        // left dangling Models.*.Provider references and forced users to fix
        // each role by hand. Rename now cascades to any role that points at
        // the old name in the same atomic write.
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["openai-compatible"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai-compatible",
                    ["Endpoint"] = "http://localhost:8000"
                }
            },
            ["Models"] = new Dictionary<string, object>
            {
                ["Main"] = new Dictionary<string, object>
                {
                    ["Provider"] = "openai-compatible",
                    ["ModelId"] = "Qwen/Qwen3.6-35B-A3B-FP8"
                }
            }
        });

        await ProviderCommand.RunAsync(
            ["provider", "rename", "openai-compatible", "my-test-provider"],
            _paths,
            output: _output);

        var output = _output.ToString();
        Assert.Contains("Renamed provider 'openai-compatible' to 'my-test-provider'.", output);
        Assert.Contains("Reassigned model role(s): Main", output);

        // Config file: Models.Main.Provider now points at the new name.
        using var doc = ReadConfigFile(_paths.NetclawConfigPath);
        var main = doc.RootElement.GetProperty("Models").GetProperty("Main");
        Assert.Equal("my-test-provider", main.GetProperty("Provider").GetString());
        Assert.Equal("Qwen/Qwen3.6-35B-A3B-FP8", main.GetProperty("ModelId").GetString());
    }

    [Fact]
    public async Task Rename_WithNoModelRefs_ReportsRenameOnly()
    {
        // A clean rename with no model roles referencing the provider should
        // not print any "Reassigned …" follow-up — that's noise.
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-vllm"] = new Dictionary<string, object> { ["Type"] = "openai-compatible" }
            }
        });

        await ProviderCommand.RunAsync(
            ["provider", "rename", "my-vllm", "lab-a100"],
            _paths,
            output: _output);

        var output = _output.ToString();
        Assert.Contains("Renamed provider 'my-vllm' to 'lab-a100'.", output);
        Assert.DoesNotContain("Reassigned", output);
    }

    [Fact]
    public void GetReferencingModelRoleEntries_ReturnsRoleAndModelId()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Models"] = new Dictionary<string, object>
            {
                ["Main"] = new Dictionary<string, object>
                {
                    ["Provider"] = "my-vllm",
                    ["ModelId"] = "Qwen/Qwen3-30B"
                },
                ["Fallback"] = new Dictionary<string, object>
                {
                    ["Provider"] = "my-ollama",
                    ["ModelId"] = "qwen3:30b"
                }
            }
        });

        var entries = ProviderCommand.GetReferencingModelRoleEntries("my-vllm", _paths);

        Assert.Single(entries);
        Assert.Equal("Main", entries[0].Role);
        Assert.Equal("Qwen/Qwen3-30B", entries[0].ModelId);
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
