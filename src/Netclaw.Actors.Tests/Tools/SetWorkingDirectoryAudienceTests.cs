// -----------------------------------------------------------------------
// <copyright file="SetWorkingDirectoryAudienceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tests.Memory;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public sealed class SetWorkingDirectoryAudienceTests
{
    private static readonly EffectivePolicyDefaults Defaults = new(
        DeploymentPosture.Personal,
        TrustAudience.Personal,
        ShellExecutionMode.HostAllowed,
        UsedStrictFallback: false);

    [Fact]
    public void SetWorkingDirectory_BlockedForPublicAudience_ByDefault()
    {
        var config = new ToolConfig();
        var policy = new ToolAccessPolicy(config, Defaults, new Netclaw.Security.ShellCommandPolicy(Netclaw.Security.ShellExecutionEnvironment.Current));
        var tool = CreateFakeTool();

        Assert.False(policy.IsToolExposed(tool, TrustAudience.Public));
    }

    [Fact]
    public void SetWorkingDirectory_AllowedForTeamAudience_ByDefault()
    {
        var config = new ToolConfig();
        var policy = new ToolAccessPolicy(config, Defaults, new Netclaw.Security.ShellCommandPolicy(Netclaw.Security.ShellExecutionEnvironment.Current));
        var tool = CreateFakeTool();

        Assert.True(policy.IsToolExposed(tool, TrustAudience.Team));
    }

    [Fact]
    public void SetWorkingDirectory_AllowedForPersonalAudience_ByDefault()
    {
        var config = new ToolConfig();
        var policy = new ToolAccessPolicy(config, Defaults, new Netclaw.Security.ShellCommandPolicy(Netclaw.Security.ShellExecutionEnvironment.Current));
        var tool = CreateFakeTool();

        Assert.True(policy.IsToolExposed(tool, TrustAudience.Personal));
    }

    private static FakeNetclawTool CreateFakeTool()
        => new("set_working_directory", "ok", "file");

}
