using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class DoctorFixServiceTests
{
    [Fact]
    public async Task PlansConfigVersionFix_WhenMissing()
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "Slack": {
                "Enabled": true
              }
            }
            """);

        var service = new DoctorFixService(paths);
        var plan = await service.BuildPlanAsync();

        Assert.True(plan.HasChanges);
        Assert.Single(plan.Fixes);
        Assert.Contains("configVersion", plan.Fixes[0].UpdatedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppliesFixPlanToDisk()
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "Slack": {
                "Enabled": true
              }
            }
            """);

        var service = new DoctorFixService(paths);
        var plan = await service.BuildPlanAsync();

        await service.ApplyAsync(plan);

        var updated = await File.ReadAllTextAsync(paths.NetclawConfigPath);
        Assert.Contains("\"configVersion\": 1", updated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddsSlackFormat_WhenSlackWebhookMissingFormat()
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

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
            """);

        var service = new DoctorFixService(paths);
        var plan = await service.BuildPlanAsync();

        Assert.True(plan.HasChanges);
        Assert.Single(plan.Fixes);
        Assert.Contains("\"Format\": \"Slack\"", plan.Fixes[0].UpdatedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovesStalePropertyViaSchemaFix()
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        // Config with a stale property that the schema no longer defines
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
            """);

        var service = new DoctorFixService(paths);
        var plan = await service.BuildPlanAsync();

        Assert.True(plan.HasChanges);
        Assert.Single(plan.Fixes);
        // CapabilityClass was removed from schema — should be cleaned up
        Assert.DoesNotContain("CapabilityClass", plan.Fixes[0].UpdatedText, StringComparison.Ordinal);
        // Other properties should be preserved
        Assert.Contains("memorizer", plan.Fixes[0].UpdatedText, StringComparison.Ordinal);
        Assert.Contains("stdio", plan.Fixes[0].UpdatedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DynamicDescriptionReflectsAppliedFixes()
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """
            {
              "Slack": {
                "Enabled": true
              }
            }
            """);

        var service = new DoctorFixService(paths);
        var plan = await service.BuildPlanAsync();

        Assert.True(plan.HasChanges);
        // Description should mention what was actually fixed
        Assert.Contains("configVersion", plan.Fixes[0].Description, StringComparison.Ordinal);
        Assert.Contains("Slack ACL defaults", plan.Fixes[0].Description, StringComparison.Ordinal);
    }

    private static string CreateTempBasePath()
    {
        var path = Path.Combine(Path.GetTempPath(), "netclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
