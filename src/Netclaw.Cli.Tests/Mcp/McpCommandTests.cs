using System.Text.Json;
using Netclaw.Cli.Mcp;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Mcp;

public sealed class McpCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;

    public McpCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        _paths = new NetclawPaths(_tempDir);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task Add_StdioServer_WritesConfig()
    {
        var args = new[] { "mcp", "add", "--transport", "stdio", "memorizer", "--", "npx", "-y", "@memorizer/mcp" };
        var exitCode = await McpCommand.RunAsync(args, _paths);

        Assert.Equal(0, exitCode);

        var config = ReadConfigFile(_paths.NetclawConfigPath);
        Assert.True(config.RootElement.TryGetProperty("McpServers", out var servers));
        Assert.True(servers.TryGetProperty("memorizer", out var entry));
        Assert.Equal("stdio", entry.GetProperty("Transport").GetString());
        Assert.Equal("npx", entry.GetProperty("Command").GetString());
    }

    [Fact]
    public async Task Add_HttpServer_WritesConfig()
    {
        var args = new[] { "mcp", "add", "--transport", "http", "textforge", "https://textforge.net/mcp" };
        var exitCode = await McpCommand.RunAsync(args, _paths);

        Assert.Equal(0, exitCode);

        var config = ReadConfigFile(_paths.NetclawConfigPath);
        Assert.True(config.RootElement.TryGetProperty("McpServers", out var servers));
        Assert.True(servers.TryGetProperty("textforge", out var entry));
        Assert.Equal("http", entry.GetProperty("Transport").GetString());
        Assert.Equal("https://textforge.net/mcp", entry.GetProperty("Url").GetString());
    }

    [Fact]
    public async Task Add_WithEnvVar_WritesSecretsFile()
    {
        var args = new[] { "mcp", "add", "--transport", "stdio", "--env", "API_KEY=secret123", "myserver", "--", "dotnet", "run" };
        var exitCode = await McpCommand.RunAsync(args, _paths);

        Assert.Equal(0, exitCode);

        // Check secrets.json has the env var
        Assert.True(File.Exists(_paths.SecretsPath));
        var secrets = ReadConfigFile(_paths.SecretsPath);
        Assert.True(secrets.RootElement.TryGetProperty("McpServers", out var mcpSecrets));
        Assert.True(mcpSecrets.TryGetProperty("myserver", out var serverSecrets));
        Assert.True(serverSecrets.TryGetProperty("EnvironmentVariables", out var envVars));
        Assert.Equal("secret123", envVars.GetProperty("API_KEY").GetString());
    }

    [Fact]
    public async Task Add_WithHeader_WritesSecretsFile()
    {
        var args = new[] { "mcp", "add", "--transport", "http", "--header", "Authorization: Bearer tok-123", "myapi", "https://api.example.com/mcp" };
        var exitCode = await McpCommand.RunAsync(args, _paths);

        Assert.Equal(0, exitCode);

        var secrets = ReadConfigFile(_paths.SecretsPath);
        Assert.True(secrets.RootElement.TryGetProperty("McpServers", out var mcpSecrets));
        Assert.True(mcpSecrets.TryGetProperty("myapi", out var serverSecrets));
        Assert.True(serverSecrets.TryGetProperty("Headers", out var headers));
        Assert.Equal("Bearer tok-123", headers.GetProperty("Authorization").GetString());
    }

    [Fact]
    public async Task List_NoServers_ShowsEmptyMessage()
    {
        var output = CaptureConsoleOutput(async () =>
            await McpCommand.RunAsync(new[] { "mcp", "list" }, _paths));

        Assert.Contains("No MCP servers configured", output);
    }

    [Fact]
    public async Task List_ShowsConfiguredServers()
    {
        // Add a server first
        await McpCommand.RunAsync(
            new[] { "mcp", "add", "--transport", "stdio", "memorizer", "--", "npx", "-y", "@memorizer/mcp" },
            _paths);

        var output = CaptureConsoleOutput(async () =>
            await McpCommand.RunAsync(new[] { "mcp", "list" }, _paths));

        Assert.Contains("memorizer", output);
        Assert.Contains("stdio", output);
    }

    [Fact]
    public async Task Get_ShowsServerDetails()
    {
        await McpCommand.RunAsync(
            new[] { "mcp", "add", "--transport", "stdio", "memorizer", "--", "npx", "-y", "@memorizer/mcp" },
            _paths);

        var output = CaptureConsoleOutput(async () =>
            await McpCommand.RunAsync(new[] { "mcp", "get", "memorizer" }, _paths));

        Assert.Contains("Name:", output);
        Assert.Contains("memorizer", output);
        Assert.Contains("Transport:", output);
        Assert.Contains("stdio", output);
    }

    [Fact]
    public async Task Get_NotFound_ReturnsError()
    {
        var exitCode = await McpCommand.RunAsync(
            new[] { "mcp", "get", "nonexistent" }, _paths);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Remove_DeletesFromConfig()
    {
        await McpCommand.RunAsync(
            new[] { "mcp", "add", "--transport", "stdio", "memorizer", "--", "npx", "-y", "@memorizer/mcp" },
            _paths);

        var exitCode = await McpCommand.RunAsync(
            new[] { "mcp", "remove", "memorizer" }, _paths);

        Assert.Equal(0, exitCode);

        // Verify it's gone
        var servers = McpCommand.LoadMcpServers(_paths);
        Assert.Empty(servers);
    }

    [Fact]
    public async Task Remove_NotFound_ReturnsError()
    {
        var exitCode = await McpCommand.RunAsync(
            new[] { "mcp", "remove", "nonexistent" }, _paths);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Disable_TogglesEnabled()
    {
        await McpCommand.RunAsync(
            new[] { "mcp", "add", "--transport", "stdio", "memorizer", "--", "npx", "-y", "@memorizer/mcp" },
            _paths);

        var exitCode = await McpCommand.RunAsync(
            new[] { "mcp", "disable", "memorizer" }, _paths);

        Assert.Equal(0, exitCode);

        var servers = McpCommand.LoadMcpServers(_paths);
        Assert.False(servers["memorizer"].Enabled);
    }

    [Fact]
    public async Task Enable_TogglesEnabled()
    {
        await McpCommand.RunAsync(
            new[] { "mcp", "add", "--transport", "stdio", "memorizer", "--", "npx", "-y", "@memorizer/mcp" },
            _paths);
        await McpCommand.RunAsync(
            new[] { "mcp", "disable", "memorizer" }, _paths);

        var exitCode = await McpCommand.RunAsync(
            new[] { "mcp", "enable", "memorizer" }, _paths);

        Assert.Equal(0, exitCode);

        var servers = McpCommand.LoadMcpServers(_paths);
        Assert.True(servers["memorizer"].Enabled);
    }

    private static JsonDocument ReadConfigFile(string path)
    {
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string CaptureConsoleOutput(Func<Task> action)
    {
        var writer = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(writer);
        try
        {
            action().GetAwaiter().GetResult();
            return writer.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }
}
