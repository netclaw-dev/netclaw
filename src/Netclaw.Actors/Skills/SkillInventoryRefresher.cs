// -----------------------------------------------------------------------
// <copyright file="SkillInventoryRefresher.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Actors.Skills;

/// <summary>
/// Rebuilds the complete skill inventory from every configured source and publishes
/// the registry and prompt index from the same scan result.
/// </summary>
public sealed class SkillInventoryRefresher
{
    private readonly object _refreshLock = new();
    private readonly NetclawPaths _paths;
    private readonly SkillFeedsConfig _feedsConfig;
    private readonly IReadOnlyList<ResolvedExternalSource> _externalSources;
    private readonly SkillRegistry _registry;
    private readonly SkillIndexPublisher _indexPublisher;

    public SkillInventoryRefresher(
        NetclawPaths paths,
        SkillFeedsConfig feedsConfig,
        IReadOnlyList<ResolvedExternalSource> externalSources,
        SkillRegistry registry,
        SkillIndexPublisher indexPublisher)
    {
        _paths = paths;
        _feedsConfig = feedsConfig;
        _externalSources = externalSources;
        _registry = registry;
        _indexPublisher = indexPublisher;
    }

    public MergedSkillScanResult Refresh()
    {
        lock (_refreshLock)
        {
            var result = SkillScanner.ScanAndMerge(
                _paths.SkillsDirectory,
                _feedsConfig.ResolveEnabledSources(_paths),
                _externalSources);

            _registry.ReplaceAll(result.AcceptedSkills, result.Issues);
            _indexPublisher.Publish();
            return result;
        }
    }
}
