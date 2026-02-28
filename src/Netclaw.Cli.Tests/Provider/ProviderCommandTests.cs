using System.Text.Json;
using Netclaw.Cli.Provider;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Provider;

public sealed class ProviderCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;
    private readonly StringWriter _output = new();

    public ProviderCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        _paths = new NetclawPaths(_tempDir);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        _output.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
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
                    ["Endpoint"] = "http://big-gpu:11434",
                    ["AuthMethod"] = "None"
                }
            }
        });

        await ProviderCommand.RunAsync(["provider", "list"], _paths, output: _output);
        var output = _output.ToString();

        Assert.Contains("my-ollama", output);
        Assert.Contains("ollama", output);
        Assert.Contains("http://big-gpu:11434", output);
    }

    [Fact]
    public async Task Add_OllamaProvider_WritesConfig()
    {
        var exitCode = await ProviderCommand.RunAsync(
            ["provider", "add", "my-ollama", "ollama", "--endpoint", "http://big-gpu:11434"],
            _paths, output: _output);

        Assert.Equal(0, exitCode);

        var config = ReadConfigFile(_paths.NetclawConfigPath);
        Assert.True(config.RootElement.TryGetProperty("Providers", out var providers));
        Assert.True(providers.TryGetProperty("my-ollama", out var entry));
        Assert.Equal("ollama", entry.GetProperty("Type").GetString());
        Assert.Equal("http://big-gpu:11434", entry.GetProperty("Endpoint").GetString());
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
        Assert.Equal("sk-or-test-123", secretEntry.GetProperty("ApiKey").GetString());
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
