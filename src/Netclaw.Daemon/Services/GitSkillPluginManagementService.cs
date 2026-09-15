// -----------------------------------------------------------------------
// <copyright file="GitSkillPluginManagementService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Daemon.Services;

/// <summary>Owns plugin source validation, reference resolution, and operator views.</summary>
internal sealed class GitSkillPluginManagementService(
    SkillFeedsConfig feedsConfig,
    NetclawPaths paths,
    IGitSkillPluginAcquirer acquirer,
    ManagedPluginStateStore stateStore,
    GitSkillPluginConfigStore configStore,
    TimeProvider timeProvider)
{
    internal sealed record InstallResult(ManagedPluginSource Source, int RestartGeneration);

    public async Task<InstallResult> InstallAsync(
        ManagedPluginApi.InstallRequest request,
        CancellationToken cancellationToken)
    {
        var repository = NormalizeRepository(request.Repository);
        var id = NormalizeId(request.SourceId, repository);
        var subdirectory = NormalizeSubdirectory(request.Subdirectory);
        ValidateInstallRequest(request);

        if (feedsConfig.Plugins.Any(
                item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new GitSkillPluginConfigException(
                GitSkillPluginConfigFailure.Conflict,
                $"Plugin '{id}' already exists.");
        }

        if (feedsConfig.Plugins.Count >= ManagedPluginSourceValidator.MaximumSourceCount)
        {
            throw new GitSkillPluginConfigException(
                GitSkillPluginConfigFailure.Conflict,
                $"No more than {ManagedPluginSourceValidator.MaximumSourceCount} GitHub plugins can be configured.");
        }

        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(request.TimeoutSeconds), timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);

        try
        {
            var source = await ResolveSourceAsync(
                request,
                id,
                repository,
                subdirectory,
                linked.Token);
            var restartGeneration = configStore.Add(source);
            return new InstallResult(source, restartGeneration);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The GitHub plugin request exceeded its configured timeout.");
        }
    }

    public async Task<ManagedPluginApi.ListResponse> ListAsync(
        CancellationToken cancellationToken)
    {
        var receipts = (await stateStore.LoadReceiptsAsync(cancellationToken))
            .ToDictionary(receipt => receipt.SourceId, StringComparer.OrdinalIgnoreCase);
        var rows = feedsConfig.Plugins.Select(source =>
        {
            receipts.TryGetValue(source.Id, out var receipt);
            var installedDirectoryExists = receipt is not null
                && Directory.Exists(paths.ManagedGitSkillCommitDirectory(
                    receipt.SourceId,
                    receipt.SourceFingerprint,
                    receipt.InstalledCommit));
            var status = !source.Enabled
                ? ManagedPluginApi.PluginStatus.Disabled
                : receipt is null || !installedDirectoryExists
                    ? ManagedPluginApi.PluginStatus.NotInstalled
                    : string.Equals(
                        receipt.SourceFingerprint,
                        ManagedPluginSourceValidator.Fingerprint(source),
                        StringComparison.Ordinal)
                        ? ManagedPluginApi.PluginStatus.Installed
                        : ManagedPluginApi.PluginStatus.Stale;

            return ToRow(source, status, receipt);
        }).ToList();

        return new ManagedPluginApi.ListResponse { Plugins = rows };
    }

    public GitSkillPluginConfigMutation SetEnabled(string name, bool enabled)
    {
        ValidateId(name);
        return configStore.SetEnabled(name, enabled);
    }

    public GitSkillPluginConfigMutation Remove(string name)
    {
        ValidateId(name);
        return configStore.Remove(name);
    }

    private async Task<ManagedPluginSource> ResolveSourceAsync(
        ManagedPluginApi.InstallRequest request,
        string id,
        string repository,
        string? subdirectory,
        CancellationToken cancellationToken)
    {
        var referenceKind = request.ReferenceKind;
        var reference = request.Reference;
        if (referenceKind == ManagedPluginApi.InstallReferenceKind.DefaultBranch)
        {
            reference = await acquirer.ResolveDefaultBranchAsync(repository, cancellationToken);
            referenceKind = ManagedPluginApi.InstallReferenceKind.Branch;
        }
        else if (referenceKind == ManagedPluginApi.InstallReferenceKind.Commit)
        {
            reference = reference!.ToLowerInvariant();
        }

        var source = NewSource(
            id,
            repository,
            request.Format,
            subdirectory,
            referenceKind == ManagedPluginApi.InstallReferenceKind.Commit
                ? ManagedPluginReferenceKind.Commit
                : ManagedPluginReferenceKind.Branch,
            reference!,
            request.TimeoutSeconds);

        if (referenceKind == ManagedPluginApi.InstallReferenceKind.Tag)
        {
            var commit = await acquirer.ResolveCommitAsync(source, cancellationToken);
            source = NewSource(
                id,
                repository,
                request.Format,
                subdirectory,
                ManagedPluginReferenceKind.Commit,
                commit,
                request.TimeoutSeconds);
        }

        if (!ManagedPluginSourceValidator.TryValidateSource(source, out var error))
            throw new InvalidOperationException(error);

        return source;
    }

    private static ManagedPluginSource NewSource(
        string id,
        string repository,
        string format,
        string? subdirectory,
        ManagedPluginReferenceKind referenceKind,
        string reference,
        int timeoutSeconds) => new()
        {
            Id = id,
            Repository = repository,
            Format = format,
            Subdirectory = subdirectory,
            ReferenceKind = referenceKind,
            Reference = reference,
            Enabled = true,
            TimeoutSeconds = timeoutSeconds,
        };

    internal static ManagedPluginApi.PluginRow ToRow(
        ManagedPluginSource source,
        ManagedPluginApi.PluginStatus status,
        ManagedPluginReceipt? receipt) => new()
        {
            SourceId = source.Id,
            ManifestName = receipt?.ManifestName,
            Repository = source.Repository,
            SourceFormat = source.Format,
            ManifestFormat = receipt?.ManifestFormat,
            Subdirectory = source.Subdirectory,
            ReferenceKind = source.ReferenceKind,
            Reference = source.Reference,
            Enabled = source.Enabled,
            Status = status,
            InstalledCommit = receipt?.InstalledCommit,
            LastObservedCommit = receipt?.LastObservedCommit,
            InstalledVersion = receipt?.InstalledVersion,
        };

    private static string NormalizeRepository(string value)
    {
        if (!ManagedPluginSourceValidator.TryNormalizeRepository(value, out var repository, out var error))
            throw new InvalidOperationException(error);
        return repository;
    }

    private static string NormalizeId(string? value, string repository)
    {
        var id = value ?? DeriveName(repository[(repository.IndexOf('/', StringComparison.Ordinal) + 1)..]);
        ValidateId(id);
        return id;
    }

    private static void ValidateId(string id)
    {
        if (!ManagedPluginSourceValidator.TryValidateId(id, out var error))
            throw new InvalidOperationException(error);
    }

    private static string? NormalizeSubdirectory(string? value)
    {
        if (!ManagedPluginSourceValidator.TryNormalizeRelativePath(
                value,
                allowEmpty: true,
                out var path,
                out var error))
        {
            throw new InvalidOperationException(error);
        }
        return path;
    }

    private static void ValidateInstallRequest(ManagedPluginApi.InstallRequest request)
    {
        if (request.Format is not ManagedPluginSourceValidator.AutoFormat
            and not ManagedPluginSourceValidator.AgentPluginFormat
            and not ManagedPluginSourceValidator.CodexFormat)
        {
            throw new InvalidOperationException("The plugin format must be 'auto', 'agent-plugin', or 'codex'.");
        }
        if (!Enum.IsDefined(request.ReferenceKind))
            throw new InvalidOperationException("The plugin reference type is not supported.");
        if (request.TimeoutSeconds is < 1 or > 300)
            throw new InvalidOperationException("The plugin timeout must be from 1 through 300 seconds.");

        if (request.ReferenceKind == ManagedPluginApi.InstallReferenceKind.DefaultBranch)
        {
            if (request.Reference is not null)
                throw new InvalidOperationException("The default branch request cannot include a reference.");
            return;
        }

        if (request.Reference is null)
            throw new InvalidOperationException("The reference is required.");

        var kind = request.ReferenceKind == ManagedPluginApi.InstallReferenceKind.Commit
            ? ManagedPluginReferenceKind.Commit
            : ManagedPluginReferenceKind.Branch;
        if (!ManagedPluginSourceValidator.TryValidateReference(kind, request.Reference, out var error))
            throw new InvalidOperationException(error);
    }

    private static string DeriveName(string repository)
    {
        var characters = repository
            .Select(character => char.IsAsciiLetterOrDigit(character)
                ? char.ToLowerInvariant(character)
                : '-')
            .ToArray();
        var words = new string(characters)
            .Split('-', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('-', words);
    }
}
