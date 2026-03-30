using Netclaw.Configuration;

namespace Netclaw.Actors.Skills;

public static class SkillRegistryUpdater
{
    public static void ApplyScanResult(
        SkillRegistry skillRegistry,
        SkillIndexContextLayer skillIndexLayer,
        SkillScanResult scanResult,
        string? skillsRoot = null)
    {
        skillRegistry.ReplaceAll(scanResult.AcceptedSkills, scanResult.Issues, skillsRoot);
        skillIndexLayer.Update(skillRegistry.GenerateDescriptionMenu());
    }
}
