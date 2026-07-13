// -----------------------------------------------------------------------
// <copyright file="SkillRegistryUpdater.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
        => ApplyMergedScanResult(
            skillRegistry,
            skillIndexLayer,
            mergedResult,
            nativeSkillsRoot,
            [],
            externalSources);

    public static void ApplyMergedScanResult(
        SkillRegistry skillRegistry,
        SkillIndexContextLayer skillIndexLayer,
        MergedSkillScanResult mergedResult,
        string nativeSkillsRoot,
        IReadOnlyList<ResolvedExternalSource> serverFeedSources,
        IReadOnlyList<ResolvedExternalSource> externalSources)
    {
        skillRegistry.ReplaceAll(mergedResult.AcceptedSkills, mergedResult.Issues);
        skillIndexLayer.Update(skillRegistry.GenerateIndex(
            nativeSkillsRoot,
            CombineIndexSources(serverFeedSources, externalSources)));
    }

    public static IReadOnlyList<ResolvedExternalSource> CombineIndexSources(
        IReadOnlyList<ResolvedExternalSource> serverFeedSources,
        IReadOnlyList<ResolvedExternalSource> externalSources)
    {
        if (serverFeedSources.Count == 0)
            return externalSources;
        if (externalSources.Count == 0)
            return serverFeedSources;

        return serverFeedSources.Concat(externalSources).ToArray();
    }
}
