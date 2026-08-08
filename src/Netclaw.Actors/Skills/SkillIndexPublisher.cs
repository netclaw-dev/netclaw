// -----------------------------------------------------------------------
// <copyright file="SkillIndexPublisher.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Skills;

public sealed class SkillIndexPublisher
{
    private readonly SkillRegistry _registry;
    private readonly SkillIndexContextLayer _contextLayer;
    private readonly Func<SkillEntry, TrustAudience, bool> _isVisible;

    public SkillIndexPublisher(
        SkillRegistry registry,
        SkillIndexContextLayer contextLayer,
        ToolAccessPolicy toolAccessPolicy)
        : this(
            registry,
            contextLayer,
            (skill, audience) => skill.Source is not McpPromptSkillSource prompt
                                 || toolAccessPolicy.IsMcpServerExposed(
                                     new McpServerName(prompt.ServerName), audience))
    {
    }

    public SkillIndexPublisher(
        SkillRegistry registry,
        SkillIndexContextLayer contextLayer,
        Func<SkillEntry, TrustAudience, bool> isVisible)
    {
        _registry = registry;
        _contextLayer = contextLayer;
        _isVisible = isVisible;
    }

    public void Publish()
    {
        _contextLayer.Update(TrustAudience.Team,
            _registry.GenerateIndex(skill => _isVisible(skill, TrustAudience.Team)));
        _contextLayer.Update(TrustAudience.Personal,
            _registry.GenerateIndex(skill => _isVisible(skill, TrustAudience.Personal)));
    }
}
