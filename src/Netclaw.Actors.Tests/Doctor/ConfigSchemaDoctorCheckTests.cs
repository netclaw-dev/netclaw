using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Doctor;

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

    private static string CreateTempBasePath()
    {
        var path = Path.Combine(Path.GetTempPath(), "netclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
