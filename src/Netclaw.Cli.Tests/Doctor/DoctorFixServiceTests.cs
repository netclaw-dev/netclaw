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

    private static string CreateTempBasePath()
    {
        var path = Path.Combine(Path.GetTempPath(), "netclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
