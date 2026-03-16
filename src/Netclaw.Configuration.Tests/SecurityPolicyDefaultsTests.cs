using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class SecurityPolicyDefaultsTests
{
    [Fact]
    public void Resolve_uses_strict_public_defaults_when_policy_missing()
    {
        var result = SecurityPolicyDefaults.Resolve(null);

        Assert.Equal(DeploymentPosture.Public, result.DeploymentPosture);
        Assert.Equal(TrustAudience.Public, result.Audience);
        Assert.Equal(ShellExecutionMode.Off, result.ShellExecutionMode);
        Assert.True(result.UsedStrictFallback);
    }

    [Fact]
    public void Resolve_uses_personal_host_shell_when_personal_posture_selected()
    {
        var result = SecurityPolicyDefaults.Resolve(new SecurityPolicyConfig
        {
            DeploymentPosture = DeploymentPosture.Personal
        });

        Assert.Equal(DeploymentPosture.Personal, result.DeploymentPosture);
        Assert.Equal(TrustAudience.Personal, result.Audience);
        Assert.Equal(ShellExecutionMode.HostAllowed, result.ShellExecutionMode);
    }

    [Fact]
    public void Resolve_honors_explicit_shell_mode_override()
    {
        var result = SecurityPolicyDefaults.Resolve(new SecurityPolicyConfig
        {
            DeploymentPosture = DeploymentPosture.Personal,
            ShellExecutionMode = ShellExecutionMode.SandboxOnly
        });

        Assert.Equal(ShellExecutionMode.SandboxOnly, result.ShellExecutionMode);
    }
}
