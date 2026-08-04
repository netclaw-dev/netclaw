// -----------------------------------------------------------------------
// <copyright file="ToolAudienceProfilesDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class ToolAudienceProfilesDoctorCheckTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public ToolAudienceProfilesDoctorCheckTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

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
    public async Task UnrestrictedPersonalProfile_Explicit_NoUnrestrictedWarning()
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

        Assert.DoesNotContain("Personal profile allows all tools", result.Message);
        Assert.DoesNotContain("explicitly sets shell_execute to Auto", result.Message);
    }

    [Fact]
    public async Task UnrestrictedPersonalProfile_Implicit_Warns()
    {
        // When Personal profile is NOT in config (fallback defaults), unrestricted
        // access should warn. AudienceProfiles must exist but Personal must be absent.
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Tools": {
                "ShellMode": "HostAllowed",
                "AudienceProfiles": {
                  "Public": {
                    "ToolsMode": "AllowList"
                  }
                }
              }
            }
            """);

        var check = new ToolAudienceProfilesDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        // Should warn about missing profiles and unrestricted fallback
        Assert.Contains("Missing explicit profiles for", result.Message);
        Assert.Contains("Personal profile allows all tools", result.Message);
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
    public async Task RecommendedProfiles_UseFailClosedShellFallback()
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

        Assert.DoesNotContain("Personal profile allows all tools", result.Message);
        Assert.DoesNotContain("explicitly sets shell_execute to Auto", result.Message);
    }

    [Fact]
    public async Task PersonalShellWithoutApprovalPolicy_DoesNotWarnAboutAutoMode()
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

        Assert.DoesNotContain("explicitly sets shell_execute to Auto", result.Message);
    }

    [Fact]
    public async Task PersonalShellWithExplicitApproval_DoesNotWarnAboutAutoMode()
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

        Assert.DoesNotContain("explicitly sets shell_execute to Auto", result.Message);
    }

    [Fact]
    public async Task PersonalShellWithoutExplicitOverride_DoesNotWarnAboutAutoMode()
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
                      "DefaultMode": "Auto"
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

        Assert.DoesNotContain("explicitly sets shell_execute to Auto", result.Message);
    }

    [Fact]
    public async Task PersonalShellWithExplicitAuto_Warns()
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
                        "shell_execute": "Auto"
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

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("explicitly sets shell_execute to Auto", result.Message);
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
    public async Task McpServerWithPerToolOverrideUnderLlmFacingKey_DoesNotTriggerWarning()
    {
        // An operator who wrote the LLM-facing alias (`notion__create-pages`)
        // into ToolOverrides — the form they saw in audit logs / transcripts
        // — still credits the server with having per-tool approval coverage.
        // Runtime now resolves both forms (see ToolApprovalConfig.TryGetExplicitMode),
        // and the doctor matches the same shape.
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
                      "ToolOverrides": { "notion__create-pages": "Approval" }
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
