// -----------------------------------------------------------------------
// <copyright file="ToolAudienceProfilesDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Config;
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

    [Theory]
    [InlineData("Personal", "HostAllowed")]
    [InlineData("Team", "Off")]
    [InlineData("Public", "Off")]
    public async Task Init_output_without_profiles_is_not_reported(string posture, string shellMode)
    {
        // `netclaw init` writes only the posture and the shell mode. An absent profile is the
        // posture default, as in the daemon, so doctor does not ask for explicit profiles.
        WriteConfig(
            $$"""
            {
              "configVersion": 1,
              "Security": { "DeploymentPosture": "{{posture}}", "ShellExecutionMode": "{{shellMode}}", "StrictDefaults": true },
              "Tools": { "ShellMode": "{{shellMode}}" }
            }
            """);

        var result = await new ToolAudienceProfilesDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(DoctorSeverity.Error, result.Severity);
        Assert.DoesNotContain("Missing explicit profiles", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AudienceProfiles is missing", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Personal profile allows all tools", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("shell_execute", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Partial_personal_profile_binds_on_the_posture_default()
    {
        // The Personal posture default allows all tools. A profile that sets only the approval
        // policy keeps that default, so an explicit Auto for shell is reported.
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Security": { "DeploymentPosture": "Personal", "ShellExecutionMode": "HostAllowed", "StrictDefaults": true },
              "Tools": {
                "ShellMode": "HostAllowed",
                "AudienceProfiles": {
                  "Personal": { "ApprovalPolicy": { "ToolOverrides": { "shell_execute": "Auto" } } }
                }
              }
            }
            """);

        var result = await new ToolAudienceProfilesDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Personal profile explicitly sets shell_execute to Auto", result.Message, StringComparison.Ordinal);
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

    // R1: a relative program grant with no folder names no single file. Doctor
    // names it so that the operator can grant the program file again.
    [Theory]
    [InlineData("""{ "shell": "Bash", "match": "TokenPrefix", "verbTokens": ["./ilspycmd"], "directory": null, "createdAt": null }""", true)]
    [InlineData("""{ "shell": "Bash", "match": "LegacyExact", "verb": "./prune.sh", "directory": "/opt/skills", "createdAt": null }""", false)]
    [InlineData("""{ "shell": "Bash", "match": "TokenPrefix", "verbTokens": ["/opt/tools/ilspycmd"], "directory": null, "createdAt": null }""", false)]
    public async Task Legacy_program_spelling_is_reported(string entry, bool reported)
    {
        WriteConfig(
            """
            { "configVersion": 1, "Tools": { "ShellMode": "HostAllowed", "AudienceProfiles": { "Personal": { "ToolsMode": "All" } } } }
            """);
        File.WriteAllText(
            _paths.ToolApprovalsPath,
            $$"""{ "version": 3, "audiences": { "personal": { "shell_execute": [ {{entry}} ] } } }""");

        var result = await new ToolAudienceProfilesDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(reported, result.Message.Contains("legacy program spelling", StringComparison.Ordinal));
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
    public async Task PersonalAllowlistWarning_NamesOnlyEnabledServersOutsideTheAllowlist()
    {
        // The shape that `netclaw mcp permissions` wrote in issue #2362.
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Security": { "DeploymentPosture": "Personal" },
              "Tools": {
                "AudienceProfiles": {
                  "Personal": {
                    "McpServersMode": "Allowlist",
                    "AllowedMcpServers": ["notion"]
                  }
                }
              },
              "McpServers": {
                "notion": { "Transport": "http", "Url": "https://mcp.notion.com/mcp", "Enabled": true },
                "textforge": { "Transport": "http", "Url": "https://textforge.net/mcp", "Enabled": true },
                "parked": { "Transport": "http", "Url": "https://example.com/mcp", "Enabled": false }
              }
            }
            """);

        var check = new ToolAudienceProfilesDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("MCP server(s) textforge are enabled, but the Personal audience cannot use them", result.Message);
    }

    [Fact]
    public async Task PersonalAllowlistWarning_DoesNotFireWhenPersonalFollowsTheDefault()
    {
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Security": { "DeploymentPosture": "Personal" },
              "Tools": {
                "AudienceProfiles": {
                  "Personal": {
                    "ApprovalPolicy": { "McpServerDefaults": { "textforge": "Auto" } }
                  }
                }
              },
              "McpServers": {
                "textforge": { "Transport": "http", "Url": "https://textforge.net/mcp", "Enabled": true }
              }
            }
            """);

        var check = new ToolAudienceProfilesDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("the Personal audience cannot use them", result.Message);
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

    [Fact]
    public async Task Legacy_default_allowlist_is_reported_with_the_fix_command()
    {
        WriteConfig(LegacyTeamConfig(
            "\"file_read\", \"file_list\", \"file_write\", \"file_edit\", \"attach_file\", "
            + "\"web_search\", \"web_fetch\", \"skill_manage\", \"set_reminder\", \"list_reminders\", "
            + "\"cancel_reminder\", \"get_reminder_history\", \"set_working_directory\""));

        var result = await new ToolAudienceProfilesDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("Tools.AudienceProfiles.Team.AllowedTools is an older Netclaw default list", result.Message, StringComparison.Ordinal);
        Assert.Contains("adds file_search, tool_output_read", result.Message, StringComparison.Ordinal);
        Assert.Contains("netclaw doctor --fix` to remove the key", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Tools.AudienceProfiles.Team.AllowedTools does not include", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_of_the_current_default_allowlist_is_reported()
    {
        WriteConfig(LegacyTeamConfig(string.Join(", ",
            ToolAudienceProfileDefaults.CurrentDefaultAllowedTools(TrustAudience.Team).Select(tool => $"\"{tool}\""))));

        var result = await new ToolAudienceProfilesDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Tools.AudienceProfiles.Team.AllowedTools is a copy of the current Netclaw default list", result.Message, StringComparison.Ordinal);
        Assert.Contains("netclaw doctor --fix` to remove the key", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Tools.AudienceProfiles.Public.AllowedTools is", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_is_reported_when_the_keys_use_other_case()
    {
        // The daemon reads configuration keys without case, so doctor does too.
        WriteConfig(LegacyTeamConfig(string.Join(", ",
                ToolAudienceProfileDefaults.CurrentDefaultAllowedTools(TrustAudience.Team).Select(tool => $"\"{tool}\"")))
            .Replace("\"Team\"", "\"team\"", StringComparison.Ordinal)
            .Replace("\"AllowedTools\": [\"file_read\", \"file_list\"", "\"allowedTools\": [\"file_read\", \"file_list\"", StringComparison.Ordinal));

        var result = await new ToolAudienceProfilesDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Tools.AudienceProfiles.Team.AllowedTools is a copy of the current Netclaw default list", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Absent_allowlist_key_is_not_reported_as_a_copy()
    {
        // An absent key binds to the current default. It is not a stored copy.
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Tools": {
                "AudienceProfiles": {
                  "Public": { "ToolsMode": "Allowlist" },
                  "Team": { "ToolsMode": "Allowlist" },
                  "Personal": { "ToolsMode": "All", "McpServersMode": "All" }
                }
              }
            }
            """);

        var result = await new ToolAudienceProfilesDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("AllowedTools is", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"file_read\"", true)]
    [InlineData("\"file_read\", \"tool_output_read\"", false)]
    public async Task Allowlist_without_tool_output_read_is_an_advisory_warning(string publicTools, bool warns)
    {
        WriteConfig(
            $$"""
            {
              "configVersion": 1,
              "Tools": {
                "AudienceProfiles": {
                  "Public": { "ToolsMode": "Allowlist", "AllowedTools": [{{publicTools}}] },
                  "Team": { "ToolsMode": "Allowlist", "AllowedTools": ["file_read", "tool_output_read"] },
                  "Personal": { "ToolsMode": "All", "McpServersMode": "All" }
                }
              }
            }
            """);

        var result = await new ToolAudienceProfilesDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(warns, result.Message.Contains(
            "Tools.AudienceProfiles.Public.AllowedTools does not include tool_output_read", StringComparison.Ordinal));
        Assert.DoesNotContain("Tools.AudienceProfiles.Team.AllowedTools does not include", result.Message, StringComparison.Ordinal);
    }

    private static string LegacyTeamConfig(string teamTools)
        => $$"""
            {
              "configVersion": 1,
              "Tools": {
                "AudienceProfiles": {
                  "Public": { "ToolsMode": "Allowlist", "AllowedTools": ["file_read"] },
                  "Team": { "ToolsMode": "Allowlist", "AllowedTools": [{{teamTools}}] },
                  "Personal": { "ToolsMode": "All", "McpServersMode": "All" }
                }
              }
            }
            """;

    [Fact]
    public async Task ControlPlaneOverrideKey_IsReportedAsInvalidAndSetsNoOverride()
    {
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Tools": {
                "AudienceProfiles": {
                  "Personal": {
                    "ApprovalPolicy": {
                      "DefaultMode": "Approval",
                      "ToolOverrides": {
                        "shell_execute": "Approval",
                        "file_write:control-plane": "Approval",
                        "file_edit:control-plane": "Deny"
                      }
                    }
                  }
                }
              }
            }
            """);

        var toolConfig = ConfigFileHelper.LoadToolConfig(_paths);
        var result = await new ToolAudienceProfilesDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        var policy = toolConfig.AudienceProfiles.Personal.ApprovalPolicy!;
        Assert.Equal(ToolApprovalMode.Approval, policy.ToolOverrides["shell_execute"]);
        Assert.DoesNotContain("file_write", policy.ToolOverrides.Keys);
        Assert.DoesNotContain("file_edit", policy.ToolOverrides.Keys);
        Assert.Equal(ToolApprovalMode.Approval, policy.GetEffectiveMode("file_write"));
        Assert.Equal(ToolApprovalMode.Approval, policy.GetEffectiveMode("file_edit"));

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains(
            "Tools.AudienceProfiles.Personal.ApprovalPolicy.ToolOverrides key 'file_edit:control-plane' is not a valid tool name and was ignored.",
            result.Message);
        Assert.Contains("key 'file_write:control-plane' is not a valid tool name and was ignored.", result.Message);
        Assert.Contains(
            "0.27.1 and earlier an override named 'file_write' was created with mode Auto, so a tool of that name may have run without approval and the mode you wrote was never applied.",
            result.Message);
        Assert.Contains("Valid forms are tool_name, server/tool, or server__tool.", result.Message);
    }

    [Theory]
    [InlineData("ToolOverrides", "mcp:memorizer:store", "an override named 'mcp' was created with mode Auto, so a tool of that name may have run without approval and the mode you wrote was never applied.")]
    [InlineData("McpServerDefaults", "notion:pages", "every tool of MCP server 'notion' was set to Auto.")]
    [InlineData("toolOVERRIDES", "mcp:memorizer:store", "an override named 'mcp' was created with mode Auto, so a tool of that name may have run without approval and the mode you wrote was never applied.")]
    public async Task ColonKey_InMcpOrToolMaps_IsReportedAsInvalid(string map, string key, string effect)
    {
        File.WriteAllText(
            _paths.NetclawConfigPath,
            $$"""
            {
              "configVersion": 1,
              "Tools": {
                "audienceprofiles": {
                  "team": { "approvalpolicy": { "{{map}}": { "{{key}}": "Approval", "shell_execute": "Deny" } } }
                }
              }
            }
            """);

        var result = await new ToolAudienceProfilesDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains($"Tools.AudienceProfiles.Team.ApprovalPolicy.{(map.Equals("McpServerDefaults", StringComparison.Ordinal) ? map : "ToolOverrides")} key '{key}'", result.Message);
        Assert.Contains($"0.27.1 and earlier {effect}", result.Message);
        Assert.DoesNotContain("shell_execute", result.Message);
    }

    [Fact]
    public async Task ColonKey_ForThePublicAudience_IsReportedAsInvalid()
    {
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Tools": {
                "AudienceProfiles": {
                  "Public": { "ApprovalPolicy": { "ToolOverrides": { "file_write:control-plane": "Approval" } } }
                }
              }
            }
            """);

        var result = await new ToolAudienceProfilesDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("Tools.AudienceProfiles.Public.ApprovalPolicy.ToolOverrides key 'file_write:control-plane'", result.Message);
    }

    [Theory]
    [InlineData("shell_execute")]
    [InlineData("notion/create-pages")]
    [InlineData("notion__create_pages")]
    public async Task ValidOverrideKeys_AreNotReported(string key)
    {
        WriteConfig(
            new
            {
                configVersion = 1,
                Tools = new { AudienceProfiles = new { Personal = new { ApprovalPolicy = new { ToolOverrides = new Dictionary<string, string> { [key] = "Approval" } } } } },
            });

        var result = await new ToolAudienceProfilesDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("not a valid tool name", result.Message);
    }

    [Fact]
    public async Task WrongCaseOverrideKey_IsAcceptedAndAppliesToTheTool()
    {
        WriteConfig(
            new
            {
                configVersion = 1,
                Tools = new { AudienceProfiles = new { Personal = new { ApprovalPolicy = new { ToolOverrides = new Dictionary<string, string> { ["File_Write"] = "Deny" } } } } },
            });

        var toolConfig = ConfigFileHelper.LoadToolConfig(_paths);
        var result = await new ToolAudienceProfilesDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ToolApprovalMode.Deny, toolConfig.AudienceProfiles.Personal.ApprovalPolicy!.GetEffectiveMode("file_write"));
        Assert.DoesNotContain("ignored", result.Message);
        Assert.Contains("key 'File_Write' now applies to tool 'file_write'", result.Message);
        Assert.NotEqual(DoctorSeverity.Error, result.Severity);
    }

    [Fact]
    public async Task OverrideKeyThatCannotBeAToolName_IsAWarningThatNamesTheKey()
    {
        WriteConfig(
            new
            {
                configVersion = 1,
                Tools = new { AudienceProfiles = new { Team = new { ApprovalPolicy = new { ToolOverrides = new Dictionary<string, string> { ["file write"] = "Deny", ["some_future_tool"] = "Deny" } } } } },
            });

        var result = await new ToolAudienceProfilesDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("Tools.AudienceProfiles.Team.ApprovalPolicy.ToolOverrides key 'file write' was ignored because it cannot be a tool name.", result.Message);
        Assert.DoesNotContain("some_future_tool", result.Message);
    }

    [Theory]
    [InlineData("shell_execute")]
    [InlineData("Shell_Execute")]
    public async Task ExplicitPersonalShellAuto_IsReportedWhenTheShellModeComesFromThePosture(string key)
    {
        // Tools.ShellMode is unset. The posture gives HostAllowed, as in the daemon.
        WriteConfig(
            $$"""
            {
              "configVersion": 1,
              "Security": { "DeploymentPosture": "Personal", "ShellExecutionMode": "HostAllowed", "StrictDefaults": true },
              "Tools": { "AudienceProfiles": { "Personal": { "ApprovalPolicy": { "ToolOverrides": { "{{key}}": "Auto" } } } } }
            }
            """);

        var result = await new ToolAudienceProfilesDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Personal profile explicitly sets shell_execute to Auto", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeysSpelledDifferentlyFromTheTool_AreReportedOncePerKey()
    {
        WriteConfig(
            """
            {
              "configVersion": 1,
              "McpServers": { "notion": { "Transport": "http", "Url": "https://example.test/mcp" } },
              "Tools": { "AudienceProfiles": { "Team": { "ApprovalPolicy": {
                "ToolOverrides": { "File_Write": "Deny", "Notion/Search": "Deny" },
                "McpServerDefaults": { "Notion": "Deny" } } } } }
            }
            """);

        var result = await new ToolAudienceProfilesDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("Tools.AudienceProfiles.Team.ApprovalPolicy.ToolOverrides key 'File_Write' now applies to tool 'file_write' (keys are matched without regard to case).", result.Message);
        Assert.Contains("ToolOverrides key 'Notion/Search' now applies to the tools of MCP server 'notion' (keys are matched without regard to case).", result.Message);
        Assert.Contains("McpServerDefaults key 'Notion' now applies to MCP server 'notion' (keys are matched without regard to case).", result.Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("\"\"")]
    public async Task OverrideKeyWithNoValue_IsReportedAsIgnored(string value)
    {
        WriteConfig(
            $$"""
            {
              "configVersion": 1,
              "Tools": { "AudienceProfiles": { "Team": { "ApprovalPolicy": { "ToolOverrides": { "file_write": {{value}} } } } } }
            }
            """);

        var result = await new ToolAudienceProfilesDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("Tools.AudienceProfiles.Team.ApprovalPolicy.ToolOverrides key 'file_write' has no value and was ignored.", result.Message);
        Assert.DoesNotContain("':'", result.Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task NullOrEmptyObjectGrantList_IsReportedAsEmptyList(string value)
    {
        WriteConfig(
            $$"""
            {
              "configVersion": 1,
              "Tools": { "AudienceProfiles": { "Team": { "AllowedTools": {{value}} } } }
            }
            """);

        var result = await new ToolAudienceProfilesDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("Tools.AudienceProfiles.Team.AllowedTools is null or an empty object", result.Message);
    }

    [Fact]
    public async Task ControlPlaneOverrideKey_PassesSchemaValidation()
    {
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Tools": {
                "AudienceProfiles": {
                  "Personal": {
                    "ApprovalPolicy": {
                      "ToolOverrides": { "file_write:control-plane": "Auto" }
                    }
                  }
                }
              }
            }
            """);

        var result = await new ConfigSchemaDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public void ConfigSchema_DoesNotAdvertiseControlPlaneOverrideKeys()
    {
        var schemaText = EmbeddedSchemaLoader.LoadConfigSchema(EmbeddedSchemaLoader.CurrentSchemaVersion);

        Assert.NotNull(schemaText);
        Assert.DoesNotContain(":control-plane", schemaText, StringComparison.Ordinal);
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
