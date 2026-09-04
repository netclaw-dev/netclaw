// -----------------------------------------------------------------------
// <copyright file="TestToolAccessPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Actors.Tools;
using Netclaw.Security;

namespace Netclaw.Actors.Tests.Tools;

internal static class TestToolAccessPolicy
{
    public static ToolAccessPolicy Create(ToolConfig config)
    {
        var shellCommandPolicy = new ShellCommandPolicy();
        var toolPathPolicy = new ToolPathPolicy([]);
        return Create(config, shellCommandPolicy, toolPathPolicy);
    }

    public static ToolAccessPolicy Create(
        ToolConfig config,
        ShellCommandPolicy shellCommandPolicy,
        ToolPathPolicy toolPathPolicy,
        NetclawPaths? paths = null) =>
        new(
            paths ?? new NetclawPaths(),
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            shellCommandPolicy,
            toolPathPolicy);
}
