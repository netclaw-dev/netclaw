// -----------------------------------------------------------------------
// <copyright file="McpManagerTestDependencies.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Skills;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Daemon.Mcp;
using Netclaw.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace Netclaw.Daemon.Tests.Mcp;

internal sealed class McpManagerTestDependencies
{
    private McpManagerTestDependencies(
        ToolConfig toolConfig,
        SkillRegistry skillRegistry,
        SkillIndexContextLayer skillIndex,
        ToolAccessPolicy toolAccessPolicy,
        SkillIndexPublisher skillIndexPublisher,
        McpArtifactMaterializer artifactMaterializer)
    {
        ToolConfig = toolConfig;
        SkillRegistry = skillRegistry;
        SkillIndex = skillIndex;
        ToolAccessPolicy = toolAccessPolicy;
        SkillIndexPublisher = skillIndexPublisher;
        ArtifactMaterializer = artifactMaterializer;
    }

    public ToolConfig ToolConfig { get; }

    public SkillRegistry SkillRegistry { get; }

    public SkillIndexContextLayer SkillIndex { get; }

    public ToolAccessPolicy ToolAccessPolicy { get; }

    public SkillIndexPublisher SkillIndexPublisher { get; }

    public McpArtifactMaterializer ArtifactMaterializer { get; }

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
        var artifactMaterializer = new McpArtifactMaterializer(
            new MagicByteContentScanner(new ContentPolicy()),
            NullLogger<McpArtifactMaterializer>.Instance);
        return new McpManagerTestDependencies(
            toolConfig,
            skillRegistry,
            skillIndex,
            toolAccessPolicy,
            skillIndexPublisher,
            artifactMaterializer);
    }
}
