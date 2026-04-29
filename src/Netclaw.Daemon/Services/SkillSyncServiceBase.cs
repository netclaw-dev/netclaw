// -----------------------------------------------------------------------
// <copyright file="SkillSyncServiceBase.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;
using Netclaw.Security.Skills;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Shared orchestration logic for skill sync services. Provides the
/// download-and-verify pipeline, per-skill sync loop, and rescan/index
/// rebuild. Subclasses supply their own hosting model (IHostedService,
/// BackgroundService) and manifest/index fetch logic.
/// </summary>
internal abstract class SkillSyncServiceBase
{
    private readonly ISkillContentScanner _scanner;
    private readonly SkillRegistry _skillRegistry;
    private readonly SkillIndexContextLayer _skillIndexLayer;

    protected SkillSyncServiceBase(
        ISkillContentScanner scanner,
        SkillRegistry skillRegistry,
        SkillIndexContextLayer skillIndexLayer)
    {
        _scanner = scanner;
        _skillRegistry = skillRegistry;
        _skillIndexLayer = skillIndexLayer;
    }

    protected abstract ILogger Logger { get; }

    /// <summary>
    /// Downloads content from <paramref name="url"/>, verifies its SHA-256
    /// against <paramref name="expectedSha256Hex"/>, and returns the content
    /// on success. Returns <c>null</c> on timeout, HTTP error, or hash
    /// mismatch — never throws for those conditions.
    /// </summary>
    protected async Task<string?> DownloadAndVerifyAsync(
        HttpClient httpClient, string url, string expectedSha256Hex, string label,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            var content = await httpClient.GetStringAsync(url, cts.Token);

            var hash = SkillSyncHelpers.ComputeSha256(content);
            if (!string.Equals(hash, expectedSha256Hex, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogWarning(
                    "SHA-256 mismatch for {Label}: expected {Expected}, got {Actual}",
                    label, expectedSha256Hex, hash);
                return null;
            }

            return content;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Logger.LogWarning("Download timed out for {Label}", label);
            return null;
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning("Download failed for {Label}: {Message}", label, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Synchronizes a single skill entry: downloads the main content and any
    /// resource files, runs content scanning on each, and atomically replaces
    /// the skill directory on disk. Returns <c>true</c> if the skill was
    /// updated and the sync state should be persisted.
    /// </summary>
    protected async Task<bool> SyncSingleSkillAsync(
        HttpClient httpClient,
        string skillName,
        string version,
        string mainUrl,
        string expectedSha256Hex,
        IReadOnlyList<SkillResourceDescriptor>? resources,
        string targetDirectory,
        SkillSyncState syncState,
        DateTimeOffset now,
        TimeSpan downloadTimeout,
        string logContext,
        CancellationToken cancellationToken)
    {
        var mainContent = await DownloadAndVerifyAsync(
            httpClient, mainUrl, expectedSha256Hex, skillName, downloadTimeout, cancellationToken);
        if (mainContent is null)
            return false;

        var mainScan = await _scanner.ScanAsync(skillName, mainContent, cancellationToken);
        if (!mainScan.IsAllowed)
        {
            Logger.LogWarning(
                "Rejected skill {SkillName} v{Version}{Context}: {Reason}",
                skillName, version, logContext, mainScan.Reason);
            return false;
        }

        var downloadedFiles = new List<DownloadedSkillFile>
        {
            new("SKILL.md", mainContent)
        };

        if (resources is { Count: > 0 })
        {
            foreach (var resource in resources)
            {
                var normalizedPath = SkillSyncHelpers.ValidateResourcePath(resource.Path);
                if (normalizedPath is null)
                {
                    Logger.LogWarning(
                        "Rejected resource path for {SkillName} v{Version}{Context}: {Path}",
                        skillName, version, logContext, resource.Path);
                    return false;
                }

                var fileContent = await DownloadAndVerifyAsync(
                    httpClient, resource.Url, resource.Sha256, $"{skillName}/{resource.Path}",
                    downloadTimeout, cancellationToken);
                if (fileContent is null)
                    return false;

                var fileScan = await _scanner.ScanAsync(
                    $"{skillName}:{normalizedPath}", fileContent, cancellationToken);
                if (!fileScan.IsAllowed)
                {
                    Logger.LogWarning(
                        "Rejected resource for {SkillName} v{Version}{Context} at {Path}: {Reason}",
                        skillName, version, logContext, normalizedPath, fileScan.Reason);
                    return false;
                }

                downloadedFiles.Add(new DownloadedSkillFile(normalizedPath, fileContent));
            }
        }

        await SkillSyncHelpers.ReplaceSkillDirectoryAsync(
            targetDirectory, skillName, downloadedFiles, cancellationToken);

        syncState.Skills[skillName] = new SyncedSkillState
        {
            Version = version,
            Sha256 = expectedSha256Hex,
            SyncedAtUtc = now
        };

        Logger.LogInformation("Synced skill {SkillName} v{Version}{Context}", skillName, version, logContext);
        return true;
    }

    /// <summary>
    /// Re-scans the skills directory tree, rebuilds the registry and context
    /// layer, and logs any rejected items.
    /// </summary>
    protected void RescanAndUpdateIndex(
        string skillsDirectory,
        IReadOnlyList<ResolvedExternalSource> serverFeedSources,
        IReadOnlyList<ResolvedExternalSource> externalSources,
        string logLabel)
    {
        var mergedResult = SkillScanner.ScanAndMerge(
            skillsDirectory, serverFeedSources, externalSources);
        SkillRegistryUpdater.ApplyMergedScanResult(
            _skillRegistry, _skillIndexLayer, mergedResult, skillsDirectory, externalSources);

        if (mergedResult.Issues.Count > 0)
        {
            Logger.LogWarning(
                "Skill inventory is degraded after {Label}: accepted={AcceptedSkillCount} rejected={RejectedIssueCount}",
                logLabel,
                mergedResult.AcceptedSkills.Count,
                mergedResult.Issues.Count);

            foreach (var issue in mergedResult.Issues)
            {
                Logger.LogWarning(
                    "Rejected skill item during {Label}: kind={IssueKind} path={Path} message={Message}",
                    logLabel,
                    issue.Kind,
                    issue.Path,
                    issue.Message);
            }
        }
        else
        {
            Logger.LogInformation(
                "Skill index updated after {Label} ({SkillCount} skills)",
                logLabel,
                mergedResult.AcceptedSkills.Count);
        }
    }
}

/// <summary>
/// Uniform descriptor for a skill resource file, abstracting over the
/// different wire types used by system feed manifests and server feed indexes.
/// </summary>
internal sealed record SkillResourceDescriptor(string Path, string Url, string Sha256);
