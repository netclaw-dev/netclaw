using System.Text.Json;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class ToolAudienceProfilesDoctorCheckTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;

    public ToolAudienceProfilesDoctorCheckTests()
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
    public async Task MissingToolsSection_IsError()
    {
        WriteConfig(new { configVersion = 1 });
        var check = new ToolAudienceProfilesDoctorCheck(_paths);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("Tools section is missing", result.Message);
    }

    [Fact]
    public async Task PublicProfileAllMode_IsError()
    {
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Tools": {
                "AudienceProfiles": {
                  "Public": {
                    "ToolsMode": "All"
                  }
                }
              }
            }
            """);

        var check = new ToolAudienceProfilesDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("public profile cannot set ToolsMode=All", result.Message);
    }

    [Fact]
    public async Task TeamFilesystemAll_IsError()
    {
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Tools": {
                "AudienceProfiles": {
                  "Team": {
                    "ReadFiles": {
                      "Mode": "All"
                    }
                  }
                }
              }
            }
            """);

        var check = new ToolAudienceProfilesDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("team profile cannot set ReadFiles.Mode=All", result.Message);
    }

    [Fact]
    public async Task UnrestrictedPersonalProfile_Warns()
    {
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Tools": {
                "ShellMode": "HostAllowed",
                "AudienceProfiles": {
                  "Personal": {
                    "ToolsMode": "All",
                    "McpServersMode": "All",
                    "ReadFiles": { "Mode": "All" },
                    "WriteFiles": { "Mode": "All" },
                    "AttachFiles": { "Mode": "All" }
                  }
                }
              }
            }
            """);

        var check = new ToolAudienceProfilesDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("Personal profile allows all tools", result.Message);
        Assert.Contains("host shell", result.Message);
    }

    [Fact]
    public async Task McpServerWithNoToolGrants_WarnsAboutSupplyChain()
    {
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Tools": {
                "AudienceProfiles": {
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
                  "Command": "npx",
                  "Enabled": true
                }
              }
            }
            """);

        var check = new ToolAudienceProfilesDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("memorizer", result.Message);
        Assert.Contains("McpServerToolGrants", result.Message);
    }

    [Fact]
    public async Task McpServerWithToolGrants_NoSupplyChainWarning()
    {
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Tools": {
                "AudienceProfiles": {
                  "Personal": {
                    "ToolsMode": "All",
                    "McpServersMode": "All",
                    "McpServerToolGrants": {
                      "memorizer": ["search_memories", "store"]
                    },
                    "ReadFiles": { "Mode": "All" },
                    "WriteFiles": { "Mode": "All" },
                    "AttachFiles": { "Mode": "All" }
                  }
                }
              },
              "McpServers": {
                "memorizer": {
                  "Transport": "stdio",
                  "Command": "npx",
                  "Enabled": true
                }
              }
            }
            """);

        var check = new ToolAudienceProfilesDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        // Should still warn about unrestricted personal, but NOT about tool grants
        Assert.DoesNotContain("McpServerToolGrants", result.Message);
    }

    [Fact]
    public async Task RecommendedProfiles_Pass()
    {
        var toolConfig = new ToolConfig
        {
            ShellMode = ShellExecutionMode.HostAllowed,
            AudienceProfiles = ToolAudienceProfileDefaults.CreateProfiles()
        };

        WriteConfig(new
        {
            configVersion = 1,
            Tools = toolConfig
        });

        var check = new ToolAudienceProfilesDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("Personal profile allows all tools", result.Message);
    }

    private void WriteConfig(object config)
    {
        File.WriteAllText(
            _paths.NetclawConfigPath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void WriteConfig(string configText)
    {
        File.WriteAllText(_paths.NetclawConfigPath, configText);
    }
}
