// -----------------------------------------------------------------------
// <copyright file="ConfigSchemaDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

[Collection(Netclaw.Cli.Tests.LegacyModelEnvironmentCollection.Name)]
public sealed class ConfigSchemaDoctorCheckTests
{
    [Fact]
    public async Task ReturnsWarning_WhenConfigFileMissing()
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Equal(CliConfigPreflight.MissingConfigMessage, result.Message);
    }

    [Fact]
    public async Task ReturnsPass_WhenConfigFileMissingButEnvConfigPresent()
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        // An env-only instance: no netclaw.json, configuration bound from
        // NETCLAW_ env vars. Schema validation applies to the file only —
        // warning "run netclaw init" here would misdiagnose a healthy daemon.
        Environment.SetEnvironmentVariable("NETCLAW_Models__Main__ModelId", "test-model");
        try
        {
            var check = new ConfigSchemaDoctorCheck(paths);
            var result = await check.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(DoctorSeverity.Pass, result.Severity);
            Assert.Contains("environment configuration detected", result.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NETCLAW_Models__Main__ModelId", null);
        }
    }

    [Fact]
    public void HasEnvironmentConfig_CountsOnlyConfigBindingVariables()
    {
        // Control variables (path/endpoint resolution) are not daemon config.
        Assert.False(DoctorJsonConfigReader.HasEnvironmentConfig(
            new System.Collections.Hashtable
            {
                ["NETCLAW_HOME"] = "/tmp/x",
                ["NETCLAW_DAEMON_ENDPOINT"] = "http://127.0.0.1:5299",
                ["UNRELATED"] = "1",
            }));

        // Double-underscore section keys are configuration binding.
        Assert.True(DoctorJsonConfigReader.HasEnvironmentConfig(
            new System.Collections.Hashtable
            {
                ["NETCLAW_Providers__eval__Type"] = "openai-compatible",
            }));
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
            """, TestContext.Current.CancellationToken);

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task ReturnsPass_WhenReverseProxyTrustedProxiesLookValid()
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Daemon": {
                "Host": "10.0.0.10",
                "ExposureMode": "reverse-proxy",
                "TrustedProxies": ["10.0.0.5", "10.0.0.0/24"]
              }
            }
            """, TestContext.Current.CancellationToken);

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task ReturnsPass_WhenSkipTunnelProcessCheck_IsBoolean()
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Daemon": {
                "ExposureMode": "tailscale-serve",
                "SkipTunnelProcessCheck": true
              }
            }
            """, TestContext.Current.CancellationToken);

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task ReturnsError_WhenTrustedProxyMalformed()
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Daemon": {
                "ExposureMode": "reverse-proxy",
                "TrustedProxies": ["not-an-ip"]
              }
            }
            """, TestContext.Current.CancellationToken);

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("TrustedProxies", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReturnsError_WhenTrustedProxyCidrMalformed()
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Daemon": {
                "ExposureMode": "reverse-proxy",
                "TrustedProxies": ["127.0.0.1/999"]
              }
            }
            """, TestContext.Current.CancellationToken);

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("TrustedProxies", result.Message, StringComparison.OrdinalIgnoreCase);
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
                "PrefillTimeoutSeconds": 1800,
                "NoProgressTimeoutSeconds": 1200
              }
            }
            """, TestContext.Current.CancellationToken);

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

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
            """, TestContext.Current.CancellationToken);

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

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
                  "Enabled": false
                }
              }
            }
            """, TestContext.Current.CancellationToken);

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

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
            """, TestContext.Current.CancellationToken);

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

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
                "PrefillTimeoutSeconds": 2
              }
            }
            """, TestContext.Current.CancellationToken);

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

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
                "PrefillTimeoutSeconds": 99999
              }
            }
            """, TestContext.Current.CancellationToken);

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
    }

    [Fact]
    public async Task ReturnsPass_WhenDaemonSectionValid()
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Daemon": {
                "Host": "0.0.0.0",
                "Port": 8443,
                "ExposureMode": "tailscale-serve"
              }
            }
            """, TestContext.Current.CancellationToken);

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task ReturnsError_WhenDaemonExposureModeInvalid()
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Daemon": {
                "ExposureMode": "kubernetes-ingress"
              }
            }
            """, TestContext.Current.CancellationToken);

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
    }

    [Fact]
    public async Task ReturnsPass_WhenDaemonSectionAbsent()
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "configVersion": 1
            }
            """, TestContext.Current.CancellationToken);

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task ReturnsPass_WhenToolsSectionMissingChannelAttachments()
    {
        // Bear-trap test for the channel-ingress-attachments migration path:
        // an existing config without any ChannelAttachments block must
        // continue to validate against schema v1. New fields on
        // ToolAudienceProfile are optional, so omitting them is legal.
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Tools": {
                "AudienceProfiles": {
                  "Public": {
                    "ToolsMode": "Allowlist",
                    "AllowedTools": ["file_read"],
                    "ReadFiles": { "Mode": "Roots", "Roots": ["{session_dir}"] }
                  },
                  "Team": {
                    "ToolsMode": "Allowlist",
                    "AllowedTools": ["file_read", "attach_file"],
                    "ReadFiles": { "Mode": "Roots", "Roots": ["{session_dir}"] }
                  },
                  "Personal": {
                    "ToolsMode": "All",
                    "ReadFiles": { "Mode": "All" }
                  }
                }
              }
            }
            """, TestContext.Current.CancellationToken);

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task ReturnsPass_WhenChannelAttachmentsBlockIsExplicit()
    {
        // Config that explicitly sets a ChannelAttachments block on one
        // profile should also validate.
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Tools": {
                "AudienceProfiles": {
                  "Public": {
                    "ChannelAttachments": {
                      "AllowedCategories": ["Image"],
                      "MaxFileBytes": 26214400,
                      "MaxFilesPerMessage": 10
                    }
                  }
                }
              }
            }
            """, TestContext.Current.CancellationToken);

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task ReturnsPass_WhenInputModalitiesIsValidCommaString()
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Models": {
                "Main": {
                  "Provider": "p",
                  "ModelId": "m",
                  "InputModalities": "Text, Image",
                  "OutputModalities": "Text"
                }
              }
            }
            """, TestContext.Current.CancellationToken);

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task ReturnsError_WhenInputModalitiesIsArray()
    {
        // Regression for #988: the legacy array form must now fail schema
        // validation so operators discover the binding mismatch instead of
        // silently getting a wrong (no-override) runtime value.
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Models": {
                "Main": {
                  "Provider": "p",
                  "ModelId": "m",
                  "InputModalities": ["Text", "Image"]
                }
              }
            }
            """, TestContext.Current.CancellationToken);

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
    }

    [Fact]
    public async Task ReturnsPass_WhenSessionMaxToolIterationsPerTurnSet()
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Session": {
                "MaxToolIterationsPerTurn": 50
              }
            }
            """, TestContext.Current.CancellationToken);

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task ReturnsError_WhenStaleSessionMaxToolCallsPerTurnPresent()
    {
        // Regression guard for the rename: the old MaxToolCallsPerTurn property
        // must be rejected as an unknown property by the Session schema, so
        // operators upgrading discover the rename via netclaw doctor instead of
        // a silently-ignored value at runtime.
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Session": {
                "MaxToolCallsPerTurn": 30
              }
            }
            """, TestContext.Current.CancellationToken);

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("MaxToolCallsPerTurn", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReturnsPass_WhenSessionTuningTimeoutResumeRetryBudgetSet()
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Session": {
                "Tuning": {
                  "TimeoutResumeRetryBudget": 3
                }
              }
            }
            """, TestContext.Current.CancellationToken);

        var check = new ConfigSchemaDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    private static string CreateTempBasePath()
    {
        var path = Path.Combine(Path.GetTempPath(), "netclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
