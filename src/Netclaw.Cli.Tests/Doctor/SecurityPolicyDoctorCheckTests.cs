// -----------------------------------------------------------------------
// <copyright file="SecurityPolicyDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class SecurityPolicyDoctorCheckTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public SecurityPolicyDoctorCheckTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

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
    public async Task ExplicitPersonalPosture_HostAllowed_Passes()
    {
        // When the user explicitly sets Personal + HostAllowed, doctor should
        // respect that intentional choice and pass cleanly.
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

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("Personal", result.Message);
    }

    [Fact]
    public async Task ImplicitPersonalPosture_HostAllowed_Warns()
    {
        // When DeploymentPosture is missing and StrictDefaults is false,
        // the fallback resolves to Personal with HostAllowed — this should
        // warn because the user didn't explicitly choose this.
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

        // StrictDefaults=false with no DeploymentPosture is an Error first
        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("silently assumes Personal posture", result.Message);
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

    [Fact]
    public async Task ToolsShellMode_DiffersFromSecurityShellMode_Warns()
    {
        WriteConfig("""
            {
              "configVersion": 1,
              "Security": { "DeploymentPosture": "Personal", "ShellExecutionMode": "HostAllowed", "StrictDefaults": true },
              "Tools": { "ShellMode": "SandboxOnly" }
            }
            """);

        var result = await new SecurityPolicyDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Equal("Security Policy", result.Name);
        Assert.Contains(
            "Tools.ShellMode is SandboxOnly but Security.ShellExecutionMode is HostAllowed. The daemon uses Tools.ShellMode.",
            result.Message);
    }

    [Fact]
    public async Task ToolsShellMode_MatchesSecurityShellMode_Passes()
    {
        WriteConfig("""
            {
              "configVersion": 1,
              "Security": { "DeploymentPosture": "Team", "ShellExecutionMode": "SandboxOnly", "StrictDefaults": true },
              "Tools": { "ShellMode": "SandboxOnly" }
            }
            """);

        var result = await new SecurityPolicyDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task ToolsShellMode_Absent_Passes()
    {
        WriteConfig("""
            {
              "configVersion": 1,
              "Security": { "DeploymentPosture": "Team", "ShellExecutionMode": "SandboxOnly", "StrictDefaults": true },
              "Tools": { }
            }
            """);

        var result = await new SecurityPolicyDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Theory]
    [InlineData("Security", "tools")]
    [InlineData("security", "Tools")]
    [InlineData("security", "tools")]
    public async Task ToolsShellMode_SectionNamesInAnyCase_Warns(string securityKey, string toolsKey)
    {
        // The daemon binds configuration keys without case, so the check must match section names without case.
        WriteConfig($$"""
            {
              "configVersion": 1,
              "{{securityKey}}": { "DeploymentPosture": "Team", "ShellExecutionMode": "Off", "StrictDefaults": true },
              "{{toolsKey}}": { "ShellMode": "HostAllowed" }
            }
            """);

        var result = await new SecurityPolicyDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("Tools.ShellMode is HostAllowed but Security.ShellExecutionMode is Off.", result.Message);
    }

    [Fact]
    public async Task ToolsShellMode_Mismatch_RemediationNamesBothFixes()
    {
        WriteConfig("""
            {
              "configVersion": 1,
              "Security": { "DeploymentPosture": "Team", "ShellExecutionMode": "SandboxOnly", "StrictDefaults": true },
              "Tools": { "ShellMode": "Off" }
            }
            """);

        var result = await new SecurityPolicyDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("Remove Tools.ShellMode so the shell mode of the posture applies", result.Remediation);
        Assert.Contains("set Tools.ShellMode and Security.ShellExecutionMode to the same value", result.Remediation);
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
