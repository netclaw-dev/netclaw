using Netclaw.Configuration;

namespace Netclaw.Actors.Skills;

public static class SkillRegistryUpdater
{
    public static void ApplyScanResult(
        SkillRegistry skillRegistry,
        SkillIndexContextLayer skillIndexLayer,
        SkillScanResult scanResult)
    {
        skillRegistry.ReplaceAll(scanResult.AcceptedSkills, scanResult.Issues);
        skillIndexLayer.Update(skillRegistry.GenerateDescriptionMenu());
    }
}
