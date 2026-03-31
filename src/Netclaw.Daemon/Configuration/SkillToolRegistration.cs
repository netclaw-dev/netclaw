using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Skills;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security.Skills;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Registers skill management tools (<c>skill_load</c>, <c>skill_read_resource</c>,
/// <c>skill_manage</c>) with the <see cref="ToolRegistry"/> after the DI container
/// is built, so that <see cref="ISkillContentScanner"/> resolves to the real
/// implementation registered by <c>AddContentSecurity()</c>.
/// </summary>
internal static class SkillToolRegistration
{
    public static void RegisterSkillTools(IServiceProvider services)
    {
        var registry = services.GetRequiredService<ToolRegistry>();
        var skillRegistry = services.GetRequiredService<SkillRegistry>();
        var skillIndexLayer = services.GetRequiredService<SkillIndexContextLayer>();
        var paths = services.GetRequiredService<NetclawPaths>();
        var scanner = services.GetRequiredService<ISkillContentScanner>();
        var externalSources = services.GetRequiredService<IReadOnlyList<ResolvedExternalSource>>();

        registry.WithSkillTools(skillRegistry, skillIndexLayer, paths, scanner, externalSources);
    }
}
