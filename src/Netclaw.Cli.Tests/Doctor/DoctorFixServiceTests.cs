// -----------------------------------------------------------------------
// <copyright file="DoctorFixServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tests.Utilities;
using Netclaw.Cli.Daemon;
using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Cli.Config;
using Netclaw.Cli.Doctor;
using Microsoft.Extensions.Configuration;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

[Collection(Netclaw.Cli.Tests.LegacyModelEnvironmentCollection.Name)]
public sealed class DoctorFixServiceTests : IDisposable
{
    private readonly DisposableTempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    // POSIX install dir: systemd units are always POSIX-style regardless of the host OS
    // running the test, and TryGetInstallDir parses forward-slash ExecStart accordingly.
    private const string InstallDir = "/opt/netclaw";

    // ── Config-file fixes (systemd PATH rehydration disabled so these stay hermetic
    //    on machines where netclaw is actually installed as a --user service) ──

    [Fact]
    public async Task MigratesLegacyModelsWithoutChangingEffectiveMetadata()
    {
        var paths = NewPaths();
        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Models": {
                "Main": {
                  "Provider": "vllm",
                  "ModelId": "qwen-vl",
                  "ContextWindow": 32768,
                  "InputModalities": "Text, Image"
                }
              }
            }
            """, TestContext.Current.CancellationToken);

        var service = ConfigOnlyService(paths);
        var plan = await service.BuildPlanAsync(TestContext.Current.CancellationToken);
        await service.ApplyAsync(plan, TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            paths.NetclawConfigPath, TestContext.Current.CancellationToken));
        var models = document.RootElement.GetProperty("Models");
        var name = models.GetProperty("Roles").GetProperty("Main").GetString()!;
        var definition = models.GetProperty("Definitions").GetProperty(name);
        Assert.Equal("qwen-vl", definition.GetProperty("ModelId").GetString());
        Assert.Equal(32768, definition.GetProperty("ContextWindow").GetInt32());
        Assert.Equal("Text, Image", definition.GetProperty("InputModalities").GetString());
        Assert.True(File.Exists(paths.NetclawConfigPath + ".legacy-models.bak"));
    }

    [Fact]
    public async Task PlansConfigVersionFix_WhenMissing()
    {
        var paths = NewPaths();
        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "Slack": {
                "Enabled": true
              }
            }
            """, TestContext.Current.CancellationToken);

        var service = ConfigOnlyService(paths);
        var plan = await service.BuildPlanAsync(TestContext.Current.CancellationToken);

        Assert.True(plan.HasChanges);
        Assert.Single(plan.Fixes);
        Assert.Contains("configVersion", plan.Fixes[0].UpdatedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppliesFixPlanToDisk()
    {
        var paths = NewPaths();
        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "Slack": {
                "Enabled": true
              }
            }
            """, TestContext.Current.CancellationToken);

        var service = ConfigOnlyService(paths);
        var plan = await service.BuildPlanAsync(TestContext.Current.CancellationToken);

        await service.ApplyAsync(plan, TestContext.Current.CancellationToken);

        var updated = await File.ReadAllTextAsync(paths.NetclawConfigPath, TestContext.Current.CancellationToken);
        Assert.Contains("\"configVersion\": 1", updated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddsSlackFormat_WhenSlackWebhookMissingFormat()
    {
        var paths = NewPaths();
        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Notifications": {
                "Webhooks": [
                  {
                    "Url": "https://hooks.slack.com/services/T00/B00/xxx"
                  }
                ]
              }
            }
            """, TestContext.Current.CancellationToken);

        var service = ConfigOnlyService(paths);
        var plan = await service.BuildPlanAsync(TestContext.Current.CancellationToken);

        Assert.True(plan.HasChanges);
        Assert.Single(plan.Fixes);
        Assert.Contains("\"Format\": \"Slack\"", plan.Fixes[0].UpdatedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovesStalePropertyViaSchemaFix()
    {
        var paths = NewPaths();
        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "McpServers": {
                "memorizer": {
                  "Transport": "stdio",
                  "Command": "uvx",
                  "Enabled": true,
                  "CapabilityClass": "MemorySafe"
                }
              }
            }
            """, TestContext.Current.CancellationToken);

        var service = ConfigOnlyService(paths);
        var plan = await service.BuildPlanAsync(TestContext.Current.CancellationToken);

        Assert.True(plan.HasChanges);
        Assert.Single(plan.Fixes);
        Assert.DoesNotContain("CapabilityClass", plan.Fixes[0].UpdatedText, StringComparison.Ordinal);
        Assert.Contains("memorizer", plan.Fixes[0].UpdatedText, StringComparison.Ordinal);
        Assert.Contains("stdio", plan.Fixes[0].UpdatedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SchemaFixPreservesNamedModelsWhenRemovingStaleProperty()
    {
        var paths = NewPaths();
        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Models": {
                "Definitions": {
                  "primary": {
                    "Provider": "example-provider",
                    "ModelId": "example-model"
                  }
                },
                "Roles": {
                  "Main": "primary"
                }
              },
              "SkillSync": {
                "Enabled": true,
                "DisableSystemSkillSync": true
              }
            }
            """, TestContext.Current.CancellationToken);

        var service = ConfigOnlyService(paths);
        var plan = await service.BuildPlanAsync(TestContext.Current.CancellationToken);

        var fix = Assert.Single(plan.Fixes);
        using var updated = JsonDocument.Parse(fix.UpdatedText);
        var root = updated.RootElement;
        var models = root.GetProperty("Models");
        Assert.Equal("example-model",
            models.GetProperty("Definitions").GetProperty("primary").GetProperty("ModelId").GetString());
        Assert.Equal("primary", models.GetProperty("Roles").GetProperty("Main").GetString());
        Assert.False(root.GetProperty("SkillSync").TryGetProperty("DisableSystemSkillSync", out _));

        await service.ApplyAsync(plan, TestContext.Current.CancellationToken);
        var result = await new ConfigSchemaDoctorCheck(paths).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task DynamicDescriptionReflectsAppliedFixes()
    {
        var paths = NewPaths();
        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "Slack": {
                "Enabled": true
              }
            }
            """, TestContext.Current.CancellationToken);

        var service = ConfigOnlyService(paths);
        var plan = await service.BuildPlanAsync(TestContext.Current.CancellationToken);

        Assert.True(plan.HasChanges);
        Assert.Contains("configVersion", plan.Fixes[0].Description, StringComparison.Ordinal);
        Assert.Contains("Slack ACL defaults", plan.Fixes[0].Description, StringComparison.Ordinal);
    }

    // ── Daemon shell-tool PATH rehydration ──

    [Fact]
    public async Task RehydratesEnvFile_WhenMissing_EvenWithoutNetclawJson()
    {
        var paths = NewPaths();
        var unitPath = WriteWiredUnit(paths);
        // No netclaw.json and no env file on disk.

        var service = new DoctorFixService(paths, unitPath, systemdEnabled: true);
        var plan = await service.BuildPlanAsync(TestContext.Current.CancellationToken);

        var fix = Assert.Single(plan.Fixes);
        Assert.Equal(paths.DaemonEnvironmentFilePath, fix.FilePath);
        Assert.StartsWith($"PATH={InstallDir}:", fix.UpdatedText, StringComparison.Ordinal);
        Assert.Contains("systemctl --user restart netclaw", fix.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RehydratesEnvFile_WhenStale_MissingInstallDir()
    {
        var paths = NewPaths();
        var unitPath = WriteWiredUnit(paths);
        await File.WriteAllTextAsync(paths.DaemonEnvironmentFilePath, "PATH=/usr/bin\n",
            TestContext.Current.CancellationToken);

        var service = new DoctorFixService(paths, unitPath, systemdEnabled: true);
        var plan = await service.BuildPlanAsync(TestContext.Current.CancellationToken);

        var fix = Assert.Single(plan.Fixes, f => f.FilePath == paths.DaemonEnvironmentFilePath);
        Assert.Equal("PATH=/usr/bin\n", fix.OriginalText);
        Assert.Contains(InstallDir, fix.UpdatedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoEnvFix_WhenHealthy()
    {
        var paths = NewPaths();
        var unitPath = WriteWiredUnit(paths);
        await File.WriteAllTextAsync(
            paths.DaemonEnvironmentFilePath,
            DaemonPathEnvironmentFile.Render(InstallDir, "/usr/bin"),
            TestContext.Current.CancellationToken);

        var service = new DoctorFixService(paths, unitPath, systemdEnabled: true);
        var plan = await service.BuildPlanAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(plan.Fixes, f => f.FilePath == paths.DaemonEnvironmentFilePath);
    }

    [Fact]
    public async Task NoEnvFix_WhenUnitIsLegacyUnwired()
    {
        // Legacy unit (inline PATH, no EnvironmentFile=) is routed to reinstall by the
        // doctor check, not rehydrated here — doctor --fix does not rewrite systemd units.
        var paths = NewPaths();
        var unitPath = WriteRawUnit(
            $"[Service]\nExecStart={InstallDir}/netclawd\nEnvironment=PATH=/opt/x:/usr/bin\n");

        var service = new DoctorFixService(paths, unitPath, systemdEnabled: true);
        var plan = await service.BuildPlanAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(plan.Fixes, f => f.FilePath == paths.DaemonEnvironmentFilePath);
    }

    [Fact]
    public async Task AppliesEnvFileRehydrationToDisk()
    {
        var paths = NewPaths();
        var unitPath = WriteWiredUnit(paths);

        var service = new DoctorFixService(paths, unitPath, systemdEnabled: true);
        var plan = await service.BuildPlanAsync(TestContext.Current.CancellationToken);
        await service.ApplyAsync(plan, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(paths.DaemonEnvironmentFilePath));
        var content = await File.ReadAllTextAsync(paths.DaemonEnvironmentFilePath, TestContext.Current.CancellationToken);
        Assert.Contains(InstallDir, content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppliesRehydration_WhenConfigDirectoryWasRemoved()
    {
        // Operator wiped ~/.netclaw/config but left the installed service. ApplyAsync must
        // recreate the parent dir instead of throwing DirectoryNotFoundException and aborting.
        var paths = NewPaths();
        var unitPath = WriteWiredUnit(paths);
        Directory.Delete(Path.GetDirectoryName(paths.DaemonEnvironmentFilePath)!, recursive: true);

        var service = new DoctorFixService(paths, unitPath, systemdEnabled: true);
        var plan = await service.BuildPlanAsync(TestContext.Current.CancellationToken);
        await service.ApplyAsync(plan, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(paths.DaemonEnvironmentFilePath));
    }

    [Fact]
    public async Task DoesNotThrow_WhenUnitEnvironmentFilePathIsMalformed()
    {
        // A hand-edited unit with an invalid EnvironmentFile= value must not crash the whole
        // doctor --fix run via Path.GetFullPath.
        var paths = NewPaths();
        var unitPath = WriteRawUnit("[Service]\nExecStart=/opt/netclaw/netclawd\nEnvironmentFile=-/bad\0path\n");

        var service = new DoctorFixService(paths, unitPath, systemdEnabled: true);
        var plan = await service.BuildPlanAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(plan.Fixes, f => f.FilePath == paths.DaemonEnvironmentFilePath);
    }

    [Fact]
    public async Task LegacyEnvironmentOverride_BlocksMigrationWithoutChangingConfig()
    {
        var paths = NewPaths();
        const string config =
            """
            {
              "configVersion": 1,
              "Models": {
                "Main": {
                  "Provider": "vllm",
                  "ModelId": "qwen-vl"
                }
              }
            }
            """;
        await File.WriteAllTextAsync(paths.NetclawConfigPath, config, TestContext.Current.CancellationToken);
        const string envVar = "NETCLAW_Models__Main__ContextWindow";
        var previous = Environment.GetEnvironmentVariable(envVar);

        try
        {
            Environment.SetEnvironmentVariable(envVar, "65536");
            var service = ConfigOnlyService(paths);

            var exception = await Assert.ThrowsAsync<ModelConfigurationException>(
                () => service.BuildPlanAsync(TestContext.Current.CancellationToken));

            Assert.Contains(envVar, exception.Message, StringComparison.Ordinal);
            Assert.Equal(config, await File.ReadAllTextAsync(
                paths.NetclawConfigPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, previous);
        }
    }

    [Fact]
    public async Task LegacyEnvironmentOverride_DoesNotBlockAlreadyNamedModels()
    {
        var paths = NewPaths();
        const string config =
            """
            {
              "configVersion": 1,
              "Models": {
                "Definitions": {
                  "main": {
                    "Provider": "vllm",
                    "ModelId": "qwen-vl"
                  }
                },
                "Roles": {
                  "Main": "main"
                }
              }
            }
            """;
        await File.WriteAllTextAsync(paths.NetclawConfigPath, config, TestContext.Current.CancellationToken);
        const string envVar = "NETCLAW_Models__Main__ContextWindow";
        var previous = Environment.GetEnvironmentVariable(envVar);

        try
        {
            Environment.SetEnvironmentVariable(envVar, "65536");
            var service = ConfigOnlyService(paths);

            var plan = await service.BuildPlanAsync(TestContext.Current.CancellationToken);

            Assert.DoesNotContain(plan.Fixes, fix => fix.FilePath == paths.NetclawConfigPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, previous);
        }
    }

    private const string Netclaw0254ToolsConfig =
        """
        {
          "configVersion": 1,
          "Tools": {
            "AudienceProfiles": {
              "Public": {
                "ToolsMode": "Allowlist",
                "AllowedTools": ["file_read", "file_list", "attach_file"]
              },
              "Team": {
                "ToolsMode": "Allowlist",
                "AllowedTools": [
                  "file_read", "file_list", "file_write", "file_edit", "attach_file",
                  "web_search", "web_fetch", "skill_manage", "set_reminder",
                  "list_reminders", "cancel_reminder", "get_reminder_history",
                  "set_working_directory"
                ]
              }
            }
          }
        }
        """;

    private const string FixName = "remove copied default audience tool lists";

    public static TheoryData<TrustAudience, int> ShippedDefaultRows()
    {
        var rows = new TheoryData<TrustAudience, int>();
        for (var row = 0; row < ToolAudienceProfileToolCatalog.LegacyPublicDefaultAllowedTools.Count; row++)
            rows.Add(TrustAudience.Public, row);
        for (var row = 0; row < ToolAudienceProfileToolCatalog.LegacyTeamDefaultAllowedTools.Count; row++)
            rows.Add(TrustAudience.Team, row);
        return rows;
    }

    // Each shipped list, which includes the current default, is deleted. The daemon applies the
    // same effective ToolConfig before and after the fix, and the rest of the profile stays.
    [Theory]
    [MemberData(nameof(ShippedDefaultRows))]
    public async Task Deletes_each_shipped_default_allowlist_and_keeps_the_bound_result(TrustAudience audience, int row)
    {
        var table = audience == TrustAudience.Public
            ? ToolAudienceProfileToolCatalog.LegacyPublicDefaultAllowedTools
            : ToolAudienceProfileToolCatalog.LegacyTeamDefaultAllowedTools;
        var original = ProfileConfig(audience, table[row]);
        var paths = NewPaths();
        await File.WriteAllTextAsync(paths.NetclawConfigPath, original, TestContext.Current.CancellationToken);
        var before = SerializeBound(BindDaemonToolConfig(paths, out _));

        var service = ConfigOnlyService(paths);
        var plan = await service.BuildPlanAsync(TestContext.Current.CancellationToken);
        await service.ApplyAsync(plan, TestContext.Current.CancellationToken);

        Assert.Contains(plan.Fixes, fix => fix.Description.Contains(FixName, StringComparison.Ordinal));
        Assert.Equal(
            original,
            await File.ReadAllTextAsync(paths.NetclawConfigPath + ".legacy-tool-defaults.bak", TestContext.Current.CancellationToken));

        var profile = ReadProfile(paths, audience);
        Assert.Null(profile["AllowedTools"]);
        Assert.Equal("Allowlist", profile["ToolsMode"]!.GetValue<string>());
        Assert.Equal("/srv/kept", profile["ReadFiles"]!["Roots"]![0]!.GetValue<string>());

        Assert.Equal(before, SerializeBound(BindDaemonToolConfig(paths, out var warnings)));
        Assert.Empty(warnings);
    }

    public static TheoryData<TrustAudience, string[]> NotShippedAllowLists()
    {
        string[] team = [.. ToolAudienceProfileDefaults.CurrentDefaultAllowedTools(TrustAudience.Team)];
        string[] publicTools = [.. ToolAudienceProfileDefaults.CurrentDefaultAllowedTools(TrustAudience.Public)];
        return new()
        {
            // One tool less, one tool more, and an empty list are operator intent.
            { TrustAudience.Team, [.. team.Where(tool => tool != ToolAudienceProfileToolCatalog.WebFetch)] },
            { TrustAudience.Public, [.. publicTools, ToolAudienceProfileToolCatalog.FileWrite] },
            { TrustAudience.Team, [.. ToolAudienceProfileToolCatalog.LegacyTeamDefaultAllowedTools[1].Skip(1)] },
            { TrustAudience.Team, [] },
            { TrustAudience.Public, [] },
        };
    }

    [Theory]
    [MemberData(nameof(NotShippedAllowLists))]
    public async Task Keeps_an_allowlist_that_is_not_a_shipped_default(TrustAudience audience, string[] tools)
    {
        var original = ProfileConfig(audience, tools);
        var paths = NewPaths();
        await File.WriteAllTextAsync(paths.NetclawConfigPath, original, TestContext.Current.CancellationToken);

        var service = ConfigOnlyService(paths);
        var plan = await service.BuildPlanAsync(TestContext.Current.CancellationToken);
        await service.ApplyAsync(plan, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(plan.Fixes, fix => fix.Description.Contains(FixName, StringComparison.Ordinal));
        Assert.Equal(tools, ReadProfile(paths, audience)["AllowedTools"]!.AsArray().Select(tool => tool!.GetValue<string>()).ToArray());
        Assert.False(File.Exists(paths.NetclawConfigPath + ".legacy-tool-defaults.bak"));
    }

    [Fact]
    public async Task Keeps_a_shipped_list_when_the_profile_is_not_in_allowlist_mode()
    {
        var paths = NewPaths();
        await File.WriteAllTextAsync(
            paths.NetclawConfigPath,
            ProfileConfig(TrustAudience.Team, ToolAudienceProfileDefaults.CurrentDefaultAllowedTools(TrustAudience.Team))
                .Replace("\"Allowlist\"", "\"All\"", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        var plan = await ConfigOnlyService(paths).BuildPlanAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(plan.Fixes, fix => fix.Description.Contains(FixName, StringComparison.Ordinal));
    }

    private static string ProfileConfig(TrustAudience audience, IEnumerable<string> tools)
        => $$"""
            {
              "configVersion": 1,
              "Tools": {
                "AudienceProfiles": {
                  "{{audience}}": {
                    "ToolsMode": "Allowlist",
                    "AllowedTools": [{{string.Join(", ", tools.Select(tool => $"\"{tool}\""))}}],
                    "ReadFiles": { "Mode": "Roots", "Roots": ["/srv/kept"] }
                  }
                }
              }
            }
            """;

    private static JsonObject ReadProfile(NetclawPaths paths, TrustAudience audience)
        => (JsonObject)JsonNode.Parse(File.ReadAllText(paths.NetclawConfigPath))!["Tools"]!["AudienceProfiles"]![audience.ToString()]!;

    // The full bound object graph, not only AllowedTools.
    private static string SerializeBound(ToolConfig toolConfig) => JsonSerializer.Serialize(toolConfig);

    [Fact]
    public async Task Existing_backup_is_kept_and_a_second_run_changes_nothing()
    {
        var paths = NewPaths();
        var firstBackup = paths.NetclawConfigPath + ".legacy-tool-defaults.bak";
        await File.WriteAllTextAsync(firstBackup, "older backup", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(paths.NetclawConfigPath, Netclaw0254ToolsConfig, TestContext.Current.CancellationToken);
        var service = ConfigOnlyService(paths);

        await service.ApplyAsync(
            await service.BuildPlanAsync(TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        var fixedText = await File.ReadAllTextAsync(paths.NetclawConfigPath, TestContext.Current.CancellationToken);
        var secondPlan = await service.BuildPlanAsync(TestContext.Current.CancellationToken);
        await service.ApplyAsync(secondPlan, TestContext.Current.CancellationToken);

        Assert.Equal("older backup", await File.ReadAllTextAsync(firstBackup, TestContext.Current.CancellationToken));
        Assert.Equal(
            Netclaw0254ToolsConfig,
            await File.ReadAllTextAsync(paths.NetclawConfigPath + ".legacy-tool-defaults.2.bak", TestContext.Current.CancellationToken));
        Assert.DoesNotContain(secondPlan.Fixes, fix => fix.FilePath == paths.NetclawConfigPath);
        Assert.Equal(fixedText, await File.ReadAllTextAsync(paths.NetclawConfigPath, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(paths.NetclawConfigPath + ".legacy-tool-defaults.3.bak"));
    }

    [Fact]
    public async Task Failed_backup_blocks_the_allowlist_deletion()
    {
        // A directory at the backup path makes the copy fail. The config file must not change.
        var paths = NewPaths();
        await File.WriteAllTextAsync(paths.NetclawConfigPath, Netclaw0254ToolsConfig, TestContext.Current.CancellationToken);
        Directory.CreateDirectory(paths.NetclawConfigPath + ".legacy-tool-defaults.bak");

        var service = ConfigOnlyService(paths);
        var plan = await service.BuildPlanAsync(TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => service.ApplyAsync(plan, TestContext.Current.CancellationToken));
        Assert.True(failure is IOException or UnauthorizedAccessException, $"Unexpected failure: {failure}");
        Assert.Equal(
            Netclaw0254ToolsConfig,
            await File.ReadAllTextAsync(paths.NetclawConfigPath, TestContext.Current.CancellationToken));
    }

    private static ToolConfig BindDaemonToolConfig(NetclawPaths paths, out IReadOnlyList<string> warnings)
    {
        var configuration = new ConfigurationBuilder()
            .AddNetclawDaemonSources(paths)
            .Build();
        var bound = PolicyConfiguration.Bind(configuration);
        warnings = bound.ToolWarnings;
        return bound.Tools;
    }

    private NetclawPaths NewPaths()
    {
        var paths = new NetclawPaths(CreateTempBasePath());
        paths.EnsureDirectoriesExist();
        return paths;
    }

    private static DoctorFixService ConfigOnlyService(NetclawPaths paths)
        => new(paths, Path.Combine(paths.BasePath, "unused.service"), systemdEnabled: false);

    private string WriteWiredUnit(NetclawPaths paths)
        // Forward-slash concatenation (NOT Path.Combine): systemd ExecStart is POSIX even
        // when the test runs on Windows, matching what TryGetInstallDir parses.
        => WriteRawUnit(DaemonManager.BuildDaemonUnitContent(
            $"{InstallDir}/netclawd",
            $"{InstallDir}/netclaw",
            paths.DaemonEnvironmentFilePath));

    private string WriteRawUnit(string content)
    {
        var dir = Path.Combine(_temp.Path, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var unitPath = Path.Combine(dir, "netclaw.service");
        File.WriteAllText(unitPath, content);
        return unitPath;
    }

    private string CreateTempBasePath()
    {
        var path = Path.Combine(_temp.Path, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
