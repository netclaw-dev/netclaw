// -----------------------------------------------------------------------
// <copyright file="DoctorFixServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class DoctorFixServiceTests
{
    // POSIX install dir: systemd units are always POSIX-style regardless of the host OS
    // running the test, and TryGetInstallDir parses forward-slash ExecStart accordingly.
    private const string InstallDir = "/opt/netclaw";

    // ── Config-file fixes (systemd PATH rehydration disabled so these stay hermetic
    //    on machines where netclaw is actually installed as a --user service) ──

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

    private static NetclawPaths NewPaths()
    {
        var paths = new NetclawPaths(CreateTempBasePath());
        paths.EnsureDirectoriesExist();
        return paths;
    }

    private static DoctorFixService ConfigOnlyService(NetclawPaths paths)
        => new(paths, Path.Combine(paths.BasePath, "unused.service"), systemdEnabled: false);

    private static string WriteWiredUnit(NetclawPaths paths)
        // Forward-slash concatenation (NOT Path.Combine): systemd ExecStart is POSIX even
        // when the test runs on Windows, matching what TryGetInstallDir parses.
        => WriteRawUnit(DaemonManager.BuildDaemonUnitContent(
            $"{InstallDir}/netclawd",
            $"{InstallDir}/netclaw",
            paths.DaemonEnvironmentFilePath));

    private static string WriteRawUnit(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "netclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var unitPath = Path.Combine(dir, "netclaw.service");
        File.WriteAllText(unitPath, content);
        return unitPath;
    }

    private static string CreateTempBasePath()
    {
        var path = Path.Combine(Path.GetTempPath(), "netclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
