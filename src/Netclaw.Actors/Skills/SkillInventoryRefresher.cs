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
    private readonly SkillIndexContextLayer _indexLayer;

    public SkillInventoryRefresher(
        NetclawPaths paths,
        SkillFeedsConfig feedsConfig,
        IReadOnlyList<ResolvedExternalSource> externalSources,
        SkillRegistry registry,
        SkillIndexContextLayer indexLayer)
    {
        _paths = paths;
        _feedsConfig = feedsConfig;
        _externalSources = externalSources;
        _registry = registry;
        _indexLayer = indexLayer;
    }

    public MergedSkillScanResult Refresh()
    {
        lock (_refreshLock)
        {
            var result = SkillScanner.ScanAndMerge(
                _paths.SkillsDirectory,
                ResolveServerFeedSources(),
                _externalSources);

            _registry.ReplaceAll(result.AcceptedSkills, result.Issues);
            _indexLayer.Update(_registry.GenerateIndex());
            return result;
        }
    }

    private IReadOnlyList<ResolvedExternalSource> ResolveServerFeedSources()
    {
        var sources = new List<ResolvedExternalSource>();
        foreach (var feed in _feedsConfig.Feeds.Where(static feed => feed.Enabled))
        {
            var directory = _paths.ServerFeedDirectory(feed.Name);
            if (Directory.Exists(directory))
            {
                sources.Add(new ResolvedExternalSource(
                    $"server-feed:{feed.Name}", [directory], AllowSymlinks: false));
            }
        }

        return sources;
    }
}
