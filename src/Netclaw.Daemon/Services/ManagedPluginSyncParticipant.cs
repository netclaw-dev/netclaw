// -----------------------------------------------------------------------
// <copyright file="ManagedPluginSyncParticipant.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Netclaw.Security.Skills;

namespace Netclaw.Daemon.Services;

internal sealed record ManagedPluginSyncResult(
    IReadOnlyList<SkillSyncResult.SourceRow> Rows,
    IReadOnlyList<ResolvedExternalSource> Sources);

/// <summary>Owns the managed plugin state machine within one shared skill sync pass.</summary>
internal sealed class ManagedPluginSyncParticipant(
    SkillFeedsConfig feedsConfig,
    NetclawPaths paths,
    TimeProvider timeProvider,
    ManagedPluginStateStore stateStore,
    IGitSkillPluginAcquirer acquirer,
    IOperationalNotificationSink notificationSink,
    ILogger logger)
{
    private bool _startupPublicationComplete;
    private bool _startupCleanupComplete;

    public async Task<IReadOnlyList<ResolvedExternalSource>?> LoadStartupSourcesAsync(
        CancellationToken cancellationToken)
    {
        if (_startupPublicationComplete)
            return null;

        var receipts = await stateStore.LoadReceiptsAsync(cancellationToken);
        _startupPublicationComplete = true;
        return ResolveSources(receipts);
    }

    public async Task<ManagedPluginSyncResult> SyncAsync(
        bool retryRejected,
        CancellationToken cancellationToken)
    {
        var rows = new List<SkillSyncResult.SourceRow>();
        var configuredSources = feedsConfig.Plugins;
        if (!ManagedPluginSourceValidator.TryValidateSources(configuredSources, out var validationError))
        {
            logger.LogWarning("Managed plugin configuration is invalid: {Error}", validationError);
            rows.Add(ConfigurationFailure());
            return new ManagedPluginSyncResult(
                rows,
                ResolveSources(await stateStore.LoadReceiptsAsync(cancellationToken)));
        }

        await stateStore.RemoveSourcesExceptAsync(
            configuredSources.Select(static source => source.Id).ToArray(),
            cancellationToken);
        var receipts = await stateStore.LoadReceiptsAsync(cancellationToken);
        if (!_startupCleanupComplete)
        {
            CleanupDirectories(receipts);
            _startupCleanupComplete = true;
        }

        foreach (var source in configuredSources.Where(static source => source.Enabled))
        {
            try
            {
                rows.Add(await SyncSourceAsync(source, retryRejected, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Managed plugin sync failed for '{PluginId}' and kept the prior publication",
                    source.Id);
                rows.Add(PluginFailure(source.Id));
            }
        }

        receipts = await stateStore.LoadReceiptsAsync(cancellationToken);
        return new ManagedPluginSyncResult(rows, ResolveSources(receipts));
    }

    private async Task<SkillSyncResult.SourceRow> SyncSourceAsync(
        ManagedPluginSource source,
        bool retryRejected,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(source.TimeoutSeconds),
            timeProvider);
        using var sourceOperation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        var sourceToken = sourceOperation.Token;
        var sourceFingerprint = ManagedPluginSourceValidator.Fingerprint(source);
        var receipt = await stateStore.GetReceiptAsync(source.Id, sourceToken);
        var commit = source.ReferenceKind == ManagedPluginReferenceKind.Commit
            ? source.Reference.ToLowerInvariant()
            : await acquirer.ResolveCommitAsync(source, sourceToken);
        var installedDirectory = receipt is null
            ? null
            : paths.ManagedGitSkillCommitDirectory(
                receipt.SourceId,
                receipt.SourceFingerprint,
                receipt.InstalledCommit);

        if (receipt is not null
            && string.Equals(receipt.SourceFingerprint, sourceFingerprint, StringComparison.Ordinal)
            && Directory.Exists(installedDirectory)
            && (string.Equals(receipt.InstalledCommit, commit, StringComparison.Ordinal)
                || string.Equals(receipt.LastObservedCommit, commit, StringComparison.Ordinal)))
        {
            return PluginUnchanged(source.Id, receipt.InstalledCommit, receipt.InstalledVersion);
        }

        var rejection = await stateStore.GetRejectionAsync(
            source.Id,
            sourceFingerprint,
            commit,
            sourceToken);
        if (rejection is not null && !retryRejected)
        {
            if (rejection.SecurityRejection
                && !rejection.AlertEmitted
                && await stateStore.TryClaimSecurityAlertAsync(
                    source.Id,
                    sourceFingerprint,
                    commit,
                    sourceToken))
            {
                EmitSecurityRejectionAlert(sourceFingerprint, commit);
            }

            logger.LogInformation(
                "Managed plugin '{PluginId}' commit {Commit} remains rejected",
                source.Id,
                commit);
            return PluginRejected(source.Id, commit);
        }

        try
        {
            var candidate = await acquirer.AcquireAsync(source, commit, sourceToken);
            if (!Directory.Exists(candidate.Directory))
                throw new IOException("The immutable managed plugin candidate is missing.");

            if (receipt is not null
                && string.Equals(receipt.SourceFingerprint, sourceFingerprint, StringComparison.Ordinal)
                && Directory.Exists(installedDirectory)
                && candidate.Version is not null
                && string.Equals(candidate.Version, receipt.InstalledVersion, StringComparison.Ordinal))
            {
                if (rejection is null)
                {
                    await stateStore.UpdateLastObservedCommitAsync(
                        source.Id,
                        candidate.Commit,
                        sourceToken);
                }
                else
                {
                    await stateStore.UpdateLastObservedCommitAfterRetryAsync(
                        source.Id,
                        sourceFingerprint,
                        candidate.Commit,
                        sourceToken);
                }
                DeleteUnpublishedCandidate(candidate.Directory, installedDirectory);
                return PluginUnchanged(source.Id, receipt.InstalledCommit, receipt.InstalledVersion);
            }

            if (rejection is null)
                await stateStore.SaveReceiptAsync(source, candidate, sourceToken);
            else
                await stateStore.SaveReceiptAfterRetryAsync(source, candidate, sourceToken);

            return new SkillSyncResult.SourceRow
            {
                Name = source.Id,
                SourceKind = SkillSyncResult.GitPluginSourceKind,
                ChangedCount = 1,
                Sidecar = "not-applicable",
                Commit = candidate.Commit,
                Version = candidate.Version,
                Notices = candidate.Notices,
            };
        }
        catch (GitSkillPluginRejectedException exception)
        {
            await stateStore.SaveRejectionAsync(
                source.Id,
                sourceFingerprint,
                exception.Commit,
                exception.Message,
                exception.SecurityRejection,
                sourceToken);

            if (exception.SecurityRejection
                && await stateStore.TryClaimSecurityAlertAsync(
                    source.Id,
                    sourceFingerprint,
                    exception.Commit,
                    sourceToken))
            {
                EmitSecurityRejectionAlert(sourceFingerprint, exception.Commit);
            }

            logger.LogWarning(
                "Managed plugin '{PluginId}' commit {Commit} was rejected",
                source.Id,
                exception.Commit);
            return PluginRejected(source.Id, exception.Commit);
        }
    }

    private IReadOnlyList<ResolvedExternalSource> ResolveSources(
        IReadOnlyList<ManagedPluginReceipt> receipts)
        => receipts
            .Where(receipt => feedsConfig.Plugins.Any(source => source.Enabled
                && string.Equals(source.Id, receipt.SourceId, StringComparison.Ordinal)))
            .Select(receipt => new
            {
                Receipt = receipt,
                Directory = paths.ManagedGitSkillCommitDirectory(
                    receipt.SourceId,
                    receipt.SourceFingerprint,
                    receipt.InstalledCommit),
            })
            .Where(static candidate => Directory.Exists(candidate.Directory))
            .OrderBy(static candidate => candidate.Receipt.SourceId, StringComparer.Ordinal)
            .Select(static candidate => new ResolvedExternalSource(
                $"managed-git:{candidate.Receipt.SourceId}",
                [candidate.Directory],
                AllowSymlinks: false))
            .ToArray();

    private void EmitSecurityRejectionAlert(string sourceFingerprint, string commit)
    {
        notificationSink.Emit(OperationalAlert.Create(
            timeProvider,
            "skill.plugin.security_rejected",
            AlertType.SkillPluginSecurityRejected,
            "A managed Git plugin failed a security check.",
            AlertSeverity.Warning,
            $"{sourceFingerprint}:{commit}",
            new Dictionary<string, string>
            {
                ["source_fingerprint"] = sourceFingerprint,
                ["commit"] = commit,
            }));
    }

    private void CleanupDirectories(IReadOnlyList<ManagedPluginReceipt> receipts)
    {
        var selectedDirectories = receipts
            .Select(receipt => Path.GetFullPath(paths.ManagedGitSkillCommitDirectory(
                receipt.SourceId,
                receipt.SourceFingerprint,
                receipt.InstalledCommit)))
            .ToHashSet(StringComparer.Ordinal);
        var root = paths.ManagedGitSkillsDirectory;
        if (!Directory.Exists(root))
            return;

        try
        {
            foreach (var sourceDirectory in Directory.EnumerateDirectories(root))
            {
                try
                {
                    CleanupSourceDirectory(sourceDirectory, selectedDirectories);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Managed plugin cleanup failed for source directory {Directory}",
                        sourceDirectory);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Managed plugin cleanup could not enumerate {Directory}", root);
        }
    }

    private void CleanupSourceDirectory(string sourceDirectory, HashSet<string> selectedDirectories)
    {
        DeleteDirectory(Path.Combine(sourceDirectory, ".staging"));
        foreach (var fingerprintDirectory in Directory.EnumerateDirectories(sourceDirectory))
        {
            if (string.Equals(Path.GetFileName(fingerprintDirectory), ".staging", StringComparison.Ordinal))
                continue;
            var commitsDirectory = Path.Combine(fingerprintDirectory, "commits");
            if (!Directory.Exists(commitsDirectory))
                continue;
            foreach (var commitDirectory in Directory.EnumerateDirectories(commitsDirectory))
            {
                if (!selectedDirectories.Contains(Path.GetFullPath(commitDirectory)))
                    DeleteDirectory(commitDirectory);
            }
        }
    }

    private void DeleteDirectory(string directory)
    {
        try
        {
            GitSkillPluginAcquirer.DeleteDirectory(directory);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Managed plugin cleanup failed for directory {Directory}", directory);
        }
    }

    private static void DeleteUnpublishedCandidate(string candidateDirectory, string installedDirectory)
    {
        if (!string.Equals(
                Path.GetFullPath(candidateDirectory),
                Path.GetFullPath(installedDirectory),
                StringComparison.Ordinal)
            && Directory.Exists(candidateDirectory))
        {
            GitSkillPluginAcquirer.DeleteDirectory(candidateDirectory);
        }
    }

    private static SkillSyncResult.SourceRow ConfigurationFailure() => new()
    {
        Name = "managed-git-plugins",
        SourceKind = SkillSyncResult.GitPluginSourceKind,
        FailedCount = 1,
        Sidecar = "not-applicable",
        Error = "The managed plugin configuration is invalid.",
    };

    private static SkillSyncResult.SourceRow PluginUnchanged(
        string sourceId,
        string commit,
        string? version) => new()
    {
        Name = sourceId,
        SourceKind = SkillSyncResult.GitPluginSourceKind,
        UnchangedCount = 1,
        Sidecar = "not-applicable",
        Commit = commit,
        Version = version,
    };

    private static SkillSyncResult.SourceRow PluginRejected(string sourceId, string commit) => new()
    {
        Name = sourceId,
        SourceKind = SkillSyncResult.GitPluginSourceKind,
        RejectedCount = 1,
        Sidecar = "not-applicable",
        Commit = commit,
        Error = "The plugin commit is rejected.",
    };

    private static SkillSyncResult.SourceRow PluginFailure(string sourceId) => new()
    {
        Name = sourceId,
        SourceKind = SkillSyncResult.GitPluginSourceKind,
        FailedCount = 1,
        Sidecar = "not-applicable",
        Error = "The source sync failed. Existing files remain in use.",
    };
}
