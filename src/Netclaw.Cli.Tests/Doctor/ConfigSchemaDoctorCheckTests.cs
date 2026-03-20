using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class ConfigSchemaDoctorCheckTests
{
    [Fact]
    public async Task ReturnsWarning_WhenConfigFileMissing()
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync();

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("not found", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReturnsPass_WhenConfigMatchesSchemaV1()
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Slack": {
                "Enabled": true,
                "MentionOnly": true,
                "AllowDirectMessages": false,
                "AllowedChannelIds": ["C123"]
              }
            }
            """);

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync();

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task ReturnsPass_WhenMemoryAndSubAgentsSectionsValid()
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Memory": {
                "RecallTimeoutMs": 500,
                "AutoRecallMaxItems": 5
              },
              "SubAgents": {
                "DefaultTimeoutSeconds": 60,
                "StoreMemoryTimeoutSeconds": 300,
                "SearchMemoriesTimeoutSeconds": 45
              }
            }
            """);

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync();

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task ReturnsPass_WhenSkillSyncSectionValid()
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "SkillSync": {
                "DisableSystemSkillSync": true
              }
            }
            """);

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync();

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task ReturnsPass_WhenToolsAudienceProfilesAndMcpCapabilityValid()
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Tools": {
                "ShellMode": "HostAllowed",
                "AudienceProfiles": {
                  "Public": {
                    "ToolsMode": "Allowlist",
                    "AllowedTools": ["file_read", "file_write", "attach_file"],
                    "McpServersMode": "Allowlist",
                    "AllowedMcpServers": [],
                    "ReadFiles": { "Mode": "Roots", "Roots": ["{session_dir}"] },
                    "WriteFiles": { "Mode": "Roots", "Roots": ["{session_dir}"] },
                    "AttachFiles": { "Mode": "Roots", "Roots": ["{session_dir}"] }
                  },
                  "Personal": {
                    "ToolsMode": "All",
                    "McpServersMode": "All",
                    "ReadFiles": { "Mode": "All" },
                    "WriteFiles": { "Mode": "All" },
                    "AttachFiles": { "Mode": "All" }
                  }
                }
              },
              "McpServers": {
                "memorizer": {
                  "Transport": "stdio",
                  "Command": "uvx",
                  "Arguments": ["memorizer-mcp"],
                  "CapabilityClass": "MemorySafe",
                  "Enabled": false
                }
              }
            }
            """);

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync();

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task ReturnsError_WhenMemoryProviderInvalid()
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Memory": {
                "Provider": "redis"
              }
            }
            """);

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync();

        Assert.Equal(DoctorSeverity.Error, result.Severity);
    }

    [Fact]
    public async Task ReturnsError_WhenSubAgentTimeoutTooLow()
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "SubAgents": {
                "DefaultTimeoutSeconds": 2
              }
            }
            """);

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync();

        Assert.Equal(DoctorSeverity.Error, result.Severity);
    }

    [Fact]
    public async Task ReturnsError_WhenSubAgentTimeoutTooHigh()
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "SubAgents": {
                "StoreMemoryTimeoutSeconds": 999
              }
            }
            """);

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync();

        Assert.Equal(DoctorSeverity.Error, result.Severity);
    }

    private static string CreateTempBasePath()
    {
        var path = Path.Combine(Path.GetTempPath(), "netclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
