// -----------------------------------------------------------------------
// <copyright file="McpManagerTestDependencies.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Skills;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;

namespace Netclaw.Daemon.Tests.Mcp;

internal sealed class McpManagerTestDependencies
{
    private McpManagerTestDependencies(
        ToolConfig toolConfig,
        SkillRegistry skillRegistry,
        SkillIndexContextLayer skillIndex,
        ToolAccessPolicy toolAccessPolicy,
        SkillIndexPublisher skillIndexPublisher)
    {
        ToolConfig = toolConfig;
        SkillRegistry = skillRegistry;
        SkillIndex = skillIndex;
        ToolAccessPolicy = toolAccessPolicy;
        SkillIndexPublisher = skillIndexPublisher;
    }

    public ToolConfig ToolConfig { get; }

    public SkillRegistry SkillRegistry { get; }

    public SkillIndexContextLayer SkillIndex { get; }

    public ToolAccessPolicy ToolAccessPolicy { get; }

    public SkillIndexPublisher SkillIndexPublisher { get; }

    public static McpManagerTestDependencies Create() => Create(new ToolConfig());

    public static McpManagerTestDependencies Create(ToolConfig toolConfig)
    {
        var skillRegistry = new SkillRegistry();
        var skillIndex = new SkillIndexContextLayer();
        var toolAccessPolicy = new ToolAccessPolicy(new NetclawPaths(),
            toolConfig,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy(),
            new ToolPathPolicy([]));
        var skillIndexPublisher = new SkillIndexPublisher(skillRegistry, skillIndex, toolAccessPolicy);
        return new McpManagerTestDependencies(
            toolConfig,
            skillRegistry,
            skillIndex,
            toolAccessPolicy,
            skillIndexPublisher);
    }
}
