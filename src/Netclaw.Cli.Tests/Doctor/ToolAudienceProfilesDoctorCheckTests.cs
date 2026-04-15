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
        Assert.Contains("without an explicit shell_execute approval gate", result.Message);
    }

    [Fact]
    public async Task PersonalShellWithoutExplicitApprovalPolicy_Warns()
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
        Assert.Contains("without an explicit shell_execute approval gate", result.Message);
    }

    [Fact]
    public async Task PersonalShellWithExplicitApprovalPolicy_DoesNotWarnAboutMissingGate()
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
                    "ApprovalPolicy": {
                      "ToolOverrides": {
                        "shell_execute": "Approval"
                      }
                    },
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

        Assert.DoesNotContain("without an explicit shell_execute approval gate", result.Message);
    }

    // ── MCP server missing Personal approval-default warning ──

    [Fact]
    public async Task McpServerWithoutPersonalApprovalDefault_TriggersWarning()
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
                      "notion": ["create-pages"]
                    },
                    "ReadFiles": { "Mode": "All" },
                    "WriteFiles": { "Mode": "All" },
                    "AttachFiles": { "Mode": "All" }
                  }
                }
              },
              "McpServers": {
                "notion": { "Transport": "http", "Url": "https://mcp.notion.com/mcp", "Enabled": true }
              }
            }
            """);

        var check = new ToolAudienceProfilesDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("notion", result.Message);
        Assert.Contains("approval default on Personal", result.Message);
        Assert.Contains("netclaw mcp permissions", result.Message);
    }

    [Fact]
    public async Task McpServerWithPersonalApprovalDefault_DoesNotTriggerWarning()
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
                    "McpServerToolGrants": { "notion": ["create-pages"] },
                    "ApprovalPolicy": {
                      "McpServerDefaults": { "notion": "Approval" }
                    },
                    "ReadFiles": { "Mode": "All" },
                    "WriteFiles": { "Mode": "All" },
                    "AttachFiles": { "Mode": "All" }
                  }
                }
              },
              "McpServers": {
                "notion": { "Transport": "http", "Url": "https://mcp.notion.com/mcp", "Enabled": true }
              }
            }
            """);

        var check = new ToolAudienceProfilesDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("approval default on Personal", result.Message);
    }

    [Fact]
    public async Task McpServerWithPerToolOverride_DoesNotTriggerWarning()
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
                    "McpServerToolGrants": { "notion": ["create-pages"] },
                    "ApprovalPolicy": {
                      "ToolOverrides": { "notion/create-pages": "Approval" }
                    },
                    "ReadFiles": { "Mode": "All" },
                    "WriteFiles": { "Mode": "All" },
                    "AttachFiles": { "Mode": "All" }
                  }
                }
              },
              "McpServers": {
                "notion": { "Transport": "http", "Url": "https://mcp.notion.com/mcp", "Enabled": true }
              }
            }
            """);

        var check = new ToolAudienceProfilesDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("approval default on Personal", result.Message);
    }

    [Fact]
    public async Task MissingApprovalWarning_DoesNotFireForServerNotInMcpServers()
    {
        // Server is in AllowedMcpServers but not in McpServers (stale allowlist).
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Tools": {
                "AudienceProfiles": {
                  "Personal": {
                    "ToolsMode": "All",
                    "McpServersMode": "All",
                    "AllowedMcpServers": ["notion"],
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

        Assert.DoesNotContain("approval default on Personal", result.Message);
    }

    [Fact]
    public async Task MissingApprovalWarning_IsWarningSeverityNotError()
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
                    "ApprovalPolicy": {
                      "ToolOverrides": { "shell_execute": "Approval" }
                    },
                    "ReadFiles": { "Mode": "Roots", "Roots": ["/tmp"] },
                    "WriteFiles": { "Mode": "Roots", "Roots": ["/tmp"] },
                    "AttachFiles": { "Mode": "Roots", "Roots": ["/tmp"] }
                  }
                }
              },
              "McpServers": {
                "notion": { "Transport": "http", "Url": "https://mcp.notion.com/mcp", "Enabled": true }
              }
            }
            """);

        var check = new ToolAudienceProfilesDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        // Warning, not error — tests the "warnings only (2)" exit code path.
        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("approval default on Personal", result.Message);
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
