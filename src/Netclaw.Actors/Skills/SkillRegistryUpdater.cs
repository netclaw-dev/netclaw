using Netclaw.Configuration;

namespace Netclaw.Actors.Skills;

public static class SkillRegistryUpdater
{
    public static void ApplyScanResult(
        SkillRegistry skillRegistry,
        SkillIndexContextLayer skillIndexLayer,
        SkillScanResult scanResult,
        string skillsRoot)
    {
        skillRegistry.ReplaceAll(scanResult.AcceptedSkills, scanResult.Issues);
        skillIndexLayer.Update(skillRegistry.GenerateIndex(skillsRoot));
    }

    public static void ApplyMergedScanResult(
        SkillRegistry skillRegistry,
        SkillIndexContextLayer skillIndexLayer,
        MergedSkillScanResult mergedResult,
        string nativeSkillsRoot,
        IReadOnlyList<ResolvedExternalSource> externalSources)
    {
        skillRegistry.ReplaceAll(mergedResult.AcceptedSkills, mergedResult.Issues);
        skillIndexLayer.Update(skillRegistry.GenerateIndex(nativeSkillsRoot, externalSources));
    }
}
