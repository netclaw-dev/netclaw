using System.Text.Json;
using System.Text.Json.Serialization;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class SecurityPolicyDoctorCheckTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;

    public SecurityPolicyDoctorCheckTests()
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
    public async Task MissingSecuritySection_IsError()
    {
        WriteConfig(new { configVersion = 1 });
        var check = new SecurityPolicyDoctorCheck(_paths);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("Security section is missing", result.Message);
    }

    [Fact]
    public async Task NullPosture_StrictDisabled_IsError()
    {
        WriteConfig("""
            {
              "configVersion": 1,
              "Security": {
                "StrictDefaults": false
              }
            }
            """);

        var check = new SecurityPolicyDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("silently assumes Personal posture", result.Message);
    }

    [Fact]
    public async Task ExplicitPersonalPosture_HostAllowed_Warns()
    {
        WriteConfig("""
            {
              "configVersion": 1,
              "Security": {
                "DeploymentPosture": "Personal",
                "ShellExecutionMode": "HostAllowed",
                "StrictDefaults": true
              }
            }
            """);

        var check = new SecurityPolicyDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("full host access", result.Message);
    }

    [Fact]
    public async Task ExplicitTeamPosture_ShellOff_Passes()
    {
        WriteConfig("""
            {
              "configVersion": 1,
              "Security": {
                "DeploymentPosture": "Team",
                "ShellExecutionMode": "Off",
                "StrictDefaults": true
              }
            }
            """);

        var check = new SecurityPolicyDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("Team", result.Message);
    }

    [Fact]
    public async Task MissingConfigFile_DelegatesToConfigReader_Warning()
    {
        // Don't write any config — DoctorJsonConfigReader returns Warning for missing file
        var check = new SecurityPolicyDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Equal("Config File", result.Name);
    }

    private void WriteConfig(object config)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        File.WriteAllText(_paths.NetclawConfigPath, JsonSerializer.Serialize(config, options));
    }

    private void WriteConfig(string configText)
    {
        File.WriteAllText(_paths.NetclawConfigPath, configText);
    }
}
