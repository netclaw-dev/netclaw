using System.Text.Json;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class McpServersDoctorCheckTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;

    public McpServersDoctorCheckTests()
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
    public async Task NoConfigFile_Passes()
    {
        var check = new McpServersDoctorCheck(_paths);
        var result = await check.RunAsync();

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task NoMcpServersSection_Passes()
    {
        WriteConfig(new { configVersion = 1 });
        var check = new McpServersDoctorCheck(_paths);

        var result = await check.RunAsync();

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("No MCP servers", result.Message);
    }

    [Fact]
    public async Task ValidStdioServer_Passes()
    {
        WriteConfig(new
        {
            configVersion = 1,
            McpServers = new
            {
                memorizer = new
                {
                    Transport = "stdio",
                    Command = "npx",
                    Arguments = new[] { "-y", "@memorizer/mcp-server" },
                    Enabled = true
                }
            }
        });

        var check = new McpServersDoctorCheck(_paths);
        var result = await check.RunAsync();

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("1 server(s) configured", result.Message);
    }

    [Fact]
    public async Task StdioServerMissingCommand_ReportsError()
    {
        WriteConfig(new
        {
            configVersion = 1,
            McpServers = new
            {
                broken = new
                {
                    Transport = "stdio",
                    Enabled = true
                }
            }
        });

        var check = new McpServersDoctorCheck(_paths);
        var result = await check.RunAsync();

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("requires 'Command'", result.Message);
    }

    [Fact]
    public async Task HttpServerMissingUrl_ReportsError()
    {
        WriteConfig(new
        {
            configVersion = 1,
            McpServers = new
            {
                broken = new
                {
                    Transport = "http",
                    Enabled = true
                }
            }
        });

        var check = new McpServersDoctorCheck(_paths);
        var result = await check.RunAsync();

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("requires 'Url'", result.Message);
    }

    [Fact]
    public async Task InvalidTransport_ReportsError()
    {
        WriteConfig(new
        {
            configVersion = 1,
            McpServers = new
            {
                broken = new
                {
                    Transport = "grpc",
                    Enabled = true
                }
            }
        });

        var check = new McpServersDoctorCheck(_paths);
        var result = await check.RunAsync();

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("invalid transport", result.Message);
    }

    [Fact]
    public async Task DisabledServer_CountsCorrectly()
    {
        WriteConfig(new
        {
            configVersion = 1,
            McpServers = new
            {
                enabled_one = new { Transport = "stdio", Command = "npx", Enabled = true },
                disabled_one = new { Transport = "stdio", Command = "npx", Enabled = false }
            }
        });

        var check = new McpServersDoctorCheck(_paths);
        var result = await check.RunAsync();

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("2 server(s) configured (1 enabled)", result.Message);
    }

    private void WriteConfig(object config)
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    }
}
