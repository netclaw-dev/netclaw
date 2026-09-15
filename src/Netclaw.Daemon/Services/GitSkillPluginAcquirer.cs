// -----------------------------------------------------------------------
// <copyright file="GitSkillPluginAcquirer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Skills;
using SecuritySkillScanResult = Netclaw.Security.Skills.SkillScanResult;

namespace Netclaw.Daemon.Services;

internal interface IGitSkillPluginAcquirer
{
    Task<ManagedPluginCandidate> AcquireAsync(ManagedPluginSource source, CancellationToken cancellationToken);

    Task<string> ResolveDefaultBranchAsync(string repository, CancellationToken cancellationToken);

    Task<string> ResolveCommitAsync(ManagedPluginSource source, CancellationToken cancellationToken);

    Task<ManagedPluginCandidate> AcquireAsync(
        ManagedPluginSource source,
        string commit,
        CancellationToken cancellationToken);
}

internal sealed class GitSkillPluginRejectedException(
    string commit,
    string message,
    bool securityRejection = false) : Exception(message)
{
    public string Commit { get; } = commit;
    public bool SecurityRejection { get; } = securityRejection;
}

/// <summary>
/// The content scanner did not establish a security verdict.
/// The sync service may retry this failure because it does not identify unsafe content.
/// </summary>
internal sealed class GitSkillPluginScannerUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal sealed record GitSkillPluginArchiveLimits(
    int MaximumManifestBytes,
    int MaximumArchiveBytes,
    long MaximumDecompressedArchiveBytes,
    int MaximumArchiveEntries,
    int MaximumSkillCount,
    int MaximumSelectedFileCount,
    int MaximumSelectedFileBytes,
    long MaximumSelectedTotalBytes)
{
    public static GitSkillPluginArchiveLimits Default { get; } = new(
        GitSkillPluginAcquirer.MaximumManifestBytes,
        GitSkillPluginAcquirer.MaximumArchiveBytes,
        GitSkillPluginAcquirer.MaximumDecompressedArchiveBytes,
        GitSkillPluginAcquirer.MaximumArchiveEntries,
        GitSkillPluginAcquirer.MaximumSkillCount,
        GitSkillPluginAcquirer.MaximumSelectedFileCount,
        GitSkillPluginAcquirer.MaximumSelectedFileBytes,
        GitSkillPluginAcquirer.MaximumSelectedTotalBytes);
}

internal sealed class GitSkillPluginAcquirer : IGitSkillPluginAcquirer
{
    internal const int MaximumManifestBytes = 256 * 1024;
    internal const int MaximumArchiveBytes = 64 * 1024 * 1024;
    internal const long MaximumDecompressedArchiveBytes = 128L * 1024 * 1024;
    internal const int MaximumArchiveEntries = 10_000;
    internal const int MaximumSkillCount = 256;
    internal const int MaximumSelectedFileCount = 4_096;
    internal const int MaximumSelectedFileBytes = 4 * 1024 * 1024;
    internal const long MaximumSelectedTotalBytes = 32L * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly HttpClient _httpClient;
    private readonly NetclawPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly ISkillContentScanner _scanner;
    private readonly GitSkillPluginArchiveLimits _limits;

    public GitSkillPluginAcquirer(
        HttpClient httpClient,
        NetclawPaths paths,
        TimeProvider timeProvider,
        ISkillContentScanner scanner)
        : this(httpClient, paths, timeProvider, scanner, GitSkillPluginArchiveLimits.Default)
    {
    }

    internal GitSkillPluginAcquirer(
        HttpClient httpClient,
        NetclawPaths paths,
        TimeProvider timeProvider,
        ISkillContentScanner scanner,
        GitSkillPluginArchiveLimits limits)
    {
        _httpClient = httpClient;
        _paths = paths;
        _timeProvider = timeProvider;
        _scanner = scanner;
        _limits = limits;
    }

    /// <summary>
    /// Creates the production handler for GitHub archive requests.
    /// The acquirer validates one allowed redirect itself.
    /// </summary>
    internal static HttpClientHandler CreateHttpHandler() => new() { AllowAutoRedirect = false };

    public async Task<ManagedPluginCandidate> AcquireAsync(
        ManagedPluginSource source,
        CancellationToken cancellationToken)
    {
        if (!ManagedPluginSourceValidator.TryValidateSource(source, out var sourceError))
            throw new InvalidOperationException(sourceError);

        var commit = await ResolveCommitAsync(source, cancellationToken);
        return await AcquireAsync(source, commit, cancellationToken);
    }

    public async Task<ManagedPluginCandidate> AcquireAsync(
        ManagedPluginSource source,
        string commit,
        CancellationToken cancellationToken)
    {
        if (!ManagedPluginSourceValidator.TryValidateSource(source, out var sourceError))
            throw new InvalidOperationException(sourceError);
        if (string.IsNullOrWhiteSpace(commit)
            || commit.Length is not (40 or 64)
            || !commit.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("The resolved commit identity is invalid.");

        commit = commit.ToLowerInvariant();
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(source.TimeoutSeconds), _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var token = linked.Token;
        try
        {
            var archivePath = Path.Combine(_paths.CacheDirectory, "git-skill-archives", $"{Guid.NewGuid():N}.tar.gz");
            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
            try
            {
                await DownloadArchiveAsync(source.Repository, commit, archivePath, token);
                var inventory = await InspectArchiveAsync(source, commit, archivePath, token);
                var sourceDirectory = _paths.ManagedGitSkillDirectory(source.Id);
                var sourceFingerprint = ManagedPluginSourceValidator.Fingerprint(source);
                var candidateDirectory = _paths.ManagedGitSkillCommitDirectory(
                    source.Id, sourceFingerprint, commit);
                var stagingDirectory = Path.Combine(
                    sourceDirectory, ".staging", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stagingDirectory);
                var createdCandidate = false;
                try
                {
                    await ExtractSelectedAsync(archivePath, inventory, stagingDirectory, commit, token);
                    var scan = SkillScanner.Scan(stagingDirectory, false, false, false);
                    if (!inventory.SkipInvalidSkills
                        && (scan.AcceptedSkills.Count == 0
                            || scan.Issues.Count > 0
                            || scan.AcceptedSkills.Count != inventory.SkillRoots.Count))
                    {
                        throw new GitSkillPluginRejectedException(
                            commit,
                            scan.Issues.FirstOrDefault()?.Message
                            ?? "One or more selected skill folders were invalid.");
                    }

                    foreach (var skill in scan.AcceptedSkills)
                    {
                        if (skill.HasSubagentRoutingMetadata)
                        {
                            throw new GitSkillPluginRejectedException(
                                commit,
                                $"Skill '{skill.Name}' declares a sub-agent route, which a plugin cannot grant.");
                        }

                        string content;
                        try
                        {
                            content = await File.ReadAllTextAsync(skill.FilePath, StrictUtf8, token);
                        }
                        catch (DecoderFallbackException)
                        {
                            throw new GitSkillPluginRejectedException(
                                commit,
                                $"Skill '{skill.Name}' is not valid UTF-8.");
                        }
                        var verdict = await ScanContentAsync(skill.Name, content, token);
                        if (!verdict.IsAllowed)
                        {
                            if (verdict.Verdict == ScanVerdict.Failed)
                            {
                                throw new GitSkillPluginScannerUnavailableException(
                                    $"The content scanner failed for skill '{skill.Name}': {verdict.Reason}");
                            }
                            throw new GitSkillPluginRejectedException(
                                commit,
                                $"The content scanner rejected skill '{skill.Name}': {verdict.Reason}",
                                securityRejection: verdict.Verdict == ScanVerdict.Rejected);
                        }

                        foreach (var resourcePath in skill.ResourcePaths ?? [])
                        {
                            var resourceBytes = await File.ReadAllBytesAsync(
                                Path.Combine(skill.SkillDirectory, resourcePath.Replace('/', Path.DirectorySeparatorChar)),
                                token);
                            string resourceContent;
                            try
                            {
                                resourceContent = StrictUtf8.GetString(resourceBytes);
                            }
                            catch (DecoderFallbackException)
                            {
                                continue;
                            }

                            var resourceVerdict = await ScanContentAsync(
                                $"{skill.Name}:{resourcePath}", resourceContent, token);
                            if (!resourceVerdict.IsAllowed)
                            {
                                if (resourceVerdict.Verdict == ScanVerdict.Failed)
                                {
                                    throw new GitSkillPluginScannerUnavailableException(
                                        $"The content scanner failed for resource '{skill.Name}:{resourcePath}': {resourceVerdict.Reason}");
                                }
                                throw new GitSkillPluginRejectedException(
                                    commit,
                                    $"The content scanner blocked resource '{skill.Name}:{resourcePath}': {resourceVerdict.Reason}",
                                    securityRejection: resourceVerdict.Verdict == ScanVerdict.Rejected);
                            }
                        }
                    }

                    ProtectDirectory(stagingDirectory);
                    Directory.CreateDirectory(Path.GetDirectoryName(candidateDirectory)!);
                    if (Directory.Exists(candidateDirectory))
                    {
                        DeleteDirectory(stagingDirectory);
                    }
                    else
                    {
                        Directory.Move(stagingDirectory, candidateDirectory);
                        createdCandidate = true;
                    }

                    var publishedScan = SkillScanner.Scan(candidateDirectory, false, false, false);
                    if (publishedScan.AcceptedSkills.Count != scan.AcceptedSkills.Count
                        || (!inventory.SkipInvalidSkills && publishedScan.Issues.Count > 0))
                    {
                        throw new InvalidDataException("The immutable candidate did not match its validated snapshot.");
                    }
                    var notices = inventory.SkipInvalidSkills
                        ? inventory.Notices.Concat(scan.Issues.Select(FormatPortableSkillNotice)).ToList()
                        : inventory.Notices.ToList();
                    if (publishedScan.AcceptedSkills.Count == 0)
                        notices.Add("The plugin contains no supported skills.");
                    return new ManagedPluginCandidate(
                        commit,
                        inventory.Format,
                        inventory.Name,
                        inventory.Version,
                        candidateDirectory,
                        publishedScan.AcceptedSkills,
                        notices);
                }
                catch
                {
                    DeleteDirectory(stagingDirectory);
                    if (createdCandidate)
                        DeleteDirectory(candidateDirectory);
                    throw;
                }
            }
            finally
            {
                File.Delete(archivePath);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The GitHub plugin request exceeded its configured timeout.");
        }
    }

    public async Task<string> ResolveCommitAsync(
        ManagedPluginSource source,
        CancellationToken cancellationToken)
    {
        if (source.ReferenceKind == ManagedPluginReferenceKind.Commit)
            return source.Reference.ToLowerInvariant();

        var reference = Uri.EscapeDataString(source.Reference);
        using var request = CreateGitHubRequest(
            HttpMethod.Get,
            $"https://api.github.com/repos/{source.Repository}/commits/{reference}");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("GitHub could not resolve the configured reference.", null, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var limited = new LimitedReadStream(stream, _limits.MaximumManifestBytes, null,
            "The GitHub commit response exceeds the JSON-size limit.");
        using var document = await JsonDocument.ParseAsync(limited, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("sha", out var shaElement)
            || shaElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("GitHub returned no commit identity.");
        }
        var commit = shaElement.GetString()!;
        if (commit.Length is not (40 or 64) || !commit.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("GitHub returned an invalid commit identity.");
        return commit.ToLowerInvariant();
    }

    private static string FormatPortableSkillNotice(SkillScanIssue issue)
        => issue.SkillName is { Length: > 0 } name
            ? $"Skill '{name}' was skipped because of {issue.Kind}."
            : $"A skill was skipped because of {issue.Kind}.";

    public async Task<string> ResolveDefaultBranchAsync(
        string repository,
        CancellationToken cancellationToken)
    {
        if (!ManagedPluginSourceValidator.TryNormalizeRepository(repository, out var normalized, out var error)
            || !string.Equals(repository, normalized, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(error.Length > 0
                ? error
                : "The repository is not canonical owner/repository form.");
        }

        using var request = CreateGitHubRequest(
            HttpMethod.Get,
            $"https://api.github.com/repos/{repository}");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("GitHub could not resolve the default branch.", null, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var limited = new LimitedReadStream(stream, _limits.MaximumManifestBytes, null,
            "The GitHub repository response exceeds the JSON-size limit.");
        using var document = await JsonDocument.ParseAsync(limited, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("default_branch", out var branchElement)
            || branchElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("GitHub returned no default branch.");
        }

        var branch = branchElement.GetString()!;
        if (!ManagedPluginSourceValidator.TryValidateReference(
                ManagedPluginReferenceKind.Branch,
                branch,
                out _))
        {
            throw new InvalidDataException("GitHub returned an invalid default branch.");
        }

        return branch;
    }

    private async Task<SecuritySkillScanResult> ScanContentAsync(
        string name,
        string content,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _scanner.ScanAsync(name, content, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new GitSkillPluginScannerUnavailableException(
                $"The content scanner failed for '{name}'.", ex);
        }
    }

    private async Task DownloadArchiveAsync(
        string repository,
        string commit,
        string destination,
        CancellationToken cancellationToken)
    {
        using var initialRequest = CreateGitHubRequest(
            HttpMethod.Get,
            $"https://api.github.com/repos/{repository}/tarball/{commit}");
        using var initial = await _httpClient.SendAsync(
            initialRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        HttpResponseMessage archiveResponse;
        if (initial.StatusCode is HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect or HttpStatusCode.MovedPermanently)
        {
            var location = initial.Headers.Location;
            if (location is null || !location.IsAbsoluteUri
                || !string.Equals(location.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(location.Host, "codeload.github.com", StringComparison.OrdinalIgnoreCase)
                || !location.IsDefaultPort || !string.IsNullOrEmpty(location.UserInfo))
            {
                throw new GitSkillPluginRejectedException(commit, "GitHub returned an unsafe archive redirect.");
            }
            using var redirectRequest = CreateGitHubRequest(HttpMethod.Get, location);
            archiveResponse = await _httpClient.SendAsync(
                redirectRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        else
        {
            archiveResponse = initial;
        }

        using (archiveResponse)
        {
            if (!archiveResponse.IsSuccessStatusCode)
                throw new HttpRequestException("GitHub could not download the commit archive.", null, archiveResponse.StatusCode);
            await using var source = await archiveResponse.Content.ReadAsStreamAsync(cancellationToken);
            await using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await CopyLimitedAsync(
                source,
                file,
                _limits.MaximumArchiveBytes,
                commit,
                "The archive exceeds the compressed-size limit.",
                cancellationToken);
        }
    }

    private static HttpRequestMessage CreateGitHubRequest(HttpMethod method, string requestUri)
        => CreateGitHubRequest(method, new Uri(requestUri, UriKind.Absolute));

    private static HttpRequestMessage CreateGitHubRequest(HttpMethod method, Uri requestUri)
    {
        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.TryAddWithoutValidation("User-Agent", NetclawUserAgent.Value);
        return request;
    }

    private async Task<ArchiveInventory> InspectArchiveAsync(
        ManagedPluginSource source,
        string commit,
        string archivePath,
        CancellationToken cancellationToken)
    {
        try
        {
            return await InspectArchiveCoreAsync(source, commit, archivePath, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            throw new GitSkillPluginRejectedException(commit, "The archive has invalid GZip or tar content.");
        }
    }

    private async Task<ArchiveInventory> InspectArchiveCoreAsync(
        ManagedPluginSource source,
        string commit,
        string archivePath,
        CancellationToken cancellationToken)
    {
        var paths = new Dictionary<string, ArchiveFile>(StringComparer.Ordinal);
        var foldedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        byte[]? agentPluginManifest = null;
        byte[]? codexManifest = null;
        var pluginRoot = source.Subdirectory is null ? "" : source.Subdirectory + "/";
        var agentPluginManifestPath = pluginRoot + "plugin.json";
        var codexManifestPath = pluginRoot + ".codex-plugin/plugin.json";
        var mcpConfigPath = pluginRoot + "mcp.json";
        var hasMcpConfig = false;
        var entryCount = 0;

        await using var file = File.OpenRead(archivePath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await using var bounded = new LimitedReadStream(
            gzip,
            _limits.MaximumDecompressedArchiveBytes,
            commit,
            "The archive exceeds the decompressed-size limit.");
        using var reader = new TarReader(bounded, leaveOpen: false);
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            entryCount++;
            if (entryCount > _limits.MaximumArchiveEntries)
                throw new GitSkillPluginRejectedException(commit, "The archive exceeds the entry-count limit.");
            if (entry.EntryType == TarEntryType.GlobalExtendedAttributes)
                continue;
            var path = NormalizeArchivePath(entry.Name, commit);
            if (path.Length == 0 && entry.EntryType == TarEntryType.Directory)
                continue;
            if (!paths.TryAdd(path, new ArchiveFile(path, entry.Length, entry.EntryType, entry.Mode))
                || !foldedPaths.Add(path))
            {
                throw new GitSkillPluginRejectedException(commit, "The archive contains duplicate normalized paths.");
            }
            if (!IsLink(entry.EntryType))
                ValidateArchiveEntry(entry, commit);
            if (string.Equals(path, mcpConfigPath, StringComparison.Ordinal))
                hasMcpConfig = true;
            if (string.Equals(path, agentPluginManifestPath, StringComparison.Ordinal)
                || string.Equals(path, codexManifestPath, StringComparison.Ordinal))
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                    || entry.Length > _limits.MaximumManifestBytes)
                {
                    throw new GitSkillPluginRejectedException(commit, "The plugin manifest is not a valid regular file.");
                }
                var manifest = await ReadLimitedAsync(
                    entry.DataStream!,
                    _limits.MaximumManifestBytes,
                    commit,
                    "The plugin manifest exceeds the size limit.",
                    cancellationToken);
                if (string.Equals(path, agentPluginManifestPath, StringComparison.Ordinal))
                    agentPluginManifest = manifest;
                else
                    codexManifest = manifest;
            }
        }

        var package = PluginPackageSelector.Select(
            source.Format,
            agentPluginManifest,
            codexManifest,
            hasMcpConfig,
            commit);
        var notices = package.Notices.ToList();
        var portableSkillsPath = pluginRoot + "skills";
        var invalidPortableSkillsLocation = package.Format == ManagedPluginSourceValidator.AgentPluginFormat
            && paths.TryGetValue(portableSkillsPath, out var portableSkillsEntry)
            && portableSkillsEntry.EntryType != TarEntryType.Directory;
        IReadOnlyList<string> roots;
        if (invalidPortableSkillsLocation)
        {
            notices.Add("The importer ignored the invalid Agent Plugins skills component.");
            roots = [];
        }
        else
        {
            roots = SelectSkillRoots(
                paths.Values,
                pluginRoot,
                package.SkillPaths,
                package.SkipInvalidSkills,
                commit);
        }
        foreach (var link in paths.Values.Where(item => item.IsLink && roots.Any(root => IsWithin(item.Path, root))))
            ValidateArchiveEntry(link, commit);
        var selected = paths.Values
            .Where(item => item.IsRegularFile && roots.Any(root => IsWithin(item.Path, root)))
            .ToArray();
        if (selected.Length > _limits.MaximumSelectedFileCount)
            throw new GitSkillPluginRejectedException(commit, "The selected plugin exceeds the file-count limit.");
        if (selected.Any(item => item.Length > _limits.MaximumSelectedFileBytes))
            throw new GitSkillPluginRejectedException(commit, "A selected plugin file exceeds the per-file limit.");
        long selectedTotal;
        try
        {
            selectedTotal = checked(selected.Sum(item => item.Length));
        }
        catch (OverflowException)
        {
            throw new GitSkillPluginRejectedException(commit, "The selected plugin exceeds the total-size limit.");
        }
        if (selectedTotal > _limits.MaximumSelectedTotalBytes)
            throw new GitSkillPluginRejectedException(commit, "The selected plugin exceeds the total-size limit.");
        return new ArchiveInventory(
            package.Format,
            package.Name,
            package.Version,
            notices,
            package.SkipInvalidSkills,
            roots,
            selected.Select(item => item.Path).ToHashSet(StringComparer.Ordinal));
    }

    private async Task ExtractSelectedAsync(
        string archivePath,
        ArchiveInventory inventory,
        string destinationRoot,
        string commit,
        CancellationToken cancellationToken)
    {
        try
        {
            await ExtractSelectedCoreAsync(
                archivePath, inventory, destinationRoot, commit, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            throw new GitSkillPluginRejectedException(commit, "The archive has invalid GZip or tar content.");
        }
    }

    private async Task ExtractSelectedCoreAsync(
        string archivePath,
        ArchiveInventory inventory,
        string destinationRoot,
        string commit,
        CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(archivePath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await using var bounded = new LimitedReadStream(
            gzip,
            _limits.MaximumDecompressedArchiveBytes,
            commit,
            "The archive exceeds the decompressed-size limit.");
        using var reader = new TarReader(bounded, leaveOpen: false);
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            if (entry.EntryType == TarEntryType.GlobalExtendedAttributes)
                continue;
            var path = NormalizeArchivePath(entry.Name, commit);
            if (!inventory.SelectedFiles.Contains(path)
                || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                continue;
            }
            var root = inventory.SkillRoots.Single(root => IsWithin(path, root));
            var relative = path[root.Length..].TrimStart('/');
            var skillName = Path.GetFileName(root.TrimEnd('/'));
            var skillRoot = Path.Combine(destinationRoot, skillName);
            var destination = Path.GetFullPath(Path.Combine(skillRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!PathUtility.IsWithinRoot(destination, skillRoot))
                throw new GitSkillPluginRejectedException(commit, "A selected path escaped its skill directory.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await CopyLimitedAsync(
                    entry.DataStream!,
                    output,
                    _limits.MaximumSelectedFileBytes,
                    commit,
                    "A selected plugin file exceeds the per-file limit.",
                    cancellationToken);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(destination, entry.Mode);
        }
    }

    private IReadOnlyList<string> SelectSkillRoots(
        IEnumerable<ArchiveFile> entries,
        string pluginRoot,
        IReadOnlyList<string> declaredPaths,
        bool allowEmpty,
        string commit)
    {
        var files = entries.Where(item => item.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile).ToArray();
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declared in declaredPaths)
        {
            if (!ManagedPluginSourceValidator.TryNormalizeRelativePath(declared, false, out var path, out var error))
                throw new GitSkillPluginRejectedException(commit, error);
            var prefix = pluginRoot + path!.TrimEnd('/');
            foreach (var item in files.Where(item => item.Path.StartsWith(prefix + "/", StringComparison.Ordinal)
                         && item.Path.EndsWith("/SKILL.md", StringComparison.Ordinal)))
            {
                var remainder = item.Path[(prefix.Length + 1)..];
                if (remainder.Count(character => character == '/') == 1)
                    roots.Add(item.Path[..^"SKILL.md".Length]);
            }
        }
        if ((!allowEmpty && roots.Count == 0) || roots.Count > _limits.MaximumSkillCount)
            throw new GitSkillPluginRejectedException(commit, "The selected plugin has no skills or exceeds the skill-count limit.");
        if (roots.GroupBy(root => Path.GetFileName(root.TrimEnd('/')), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new GitSkillPluginRejectedException(commit, "Selected skills have duplicate directory names.");
        var orderedRoots = roots.Order(StringComparer.Ordinal).ToArray();
        if (orderedRoots.Any(parent => orderedRoots.Any(child => child.Length > parent.Length
                && child.StartsWith(parent, StringComparison.Ordinal))))
        {
            throw new GitSkillPluginRejectedException(commit, "Selected skill directories cannot overlap.");
        }
        return orderedRoots;
    }

    private static string NormalizeArchivePath(string value, string commit)
    {
        if (value.Contains('\\', StringComparison.Ordinal)
            || value.StartsWith("/", StringComparison.Ordinal) || value.Contains("//", StringComparison.Ordinal)
            || value.Any(char.IsControl))
            throw new GitSkillPluginRejectedException(commit, "The archive contains an unsafe path.");
        var segments = value.TrimEnd('/').Split('/');
        if (segments.Length == 0 || segments.Any(segment => segment.Length is 0 or > 255 || segment is "." or ".."))
            throw new GitSkillPluginRejectedException(commit, "The archive contains an unsafe path segment.");
        var relative = string.Join('/', segments.Skip(1));
        if (relative.Length > 512)
            throw new GitSkillPluginRejectedException(commit, "The archive contains a path that exceeds 512 characters.");
        if (relative.Length > 0
            && (!ManagedPluginSourceValidator.TryNormalizeRelativePath(
                    relative, false, out var normalized, out _)
                || !string.Equals(relative, normalized, StringComparison.Ordinal)))
        {
            throw new GitSkillPluginRejectedException(commit, "The archive contains an unsafe or nonportable path segment.");
        }
        return relative;
    }

    private static void ValidateArchiveEntry(TarEntry entry, string commit)
    {
        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory))
            throw new GitSkillPluginRejectedException(commit, "The archive contains a link or special file.");
        if (entry.Length < 0)
            throw new GitSkillPluginRejectedException(commit, "The archive contains an invalid file length.");
        if (((int)entry.Mode & ~0x1FF) != 0)
            throw new GitSkillPluginRejectedException(commit, "The archive contains unsupported mode bits.");
    }

    private static void ValidateArchiveEntry(ArchiveFile entry, string commit)
    {
        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory))
            throw new GitSkillPluginRejectedException(commit, "The archive contains a link or special file.");
        if (entry.Length < 0)
            throw new GitSkillPluginRejectedException(commit, "The archive contains an invalid file length.");
        if (((int)entry.Mode & ~0x1FF) != 0)
            throw new GitSkillPluginRejectedException(commit, "The archive contains unsupported mode bits.");
    }

    private static bool IsLink(TarEntryType entryType)
        => entryType is TarEntryType.SymbolicLink or TarEntryType.HardLink;

    private static bool IsWithin(string path, string root) => path.StartsWith(root, StringComparison.Ordinal);

    private static async Task<byte[]> ReadLimitedAsync(
        Stream stream,
        int maximumBytes,
        string commit,
        string limitMessage,
        CancellationToken cancellationToken)
    {
        await using var output = new MemoryStream();
        await CopyLimitedAsync(stream, output, maximumBytes, commit, limitMessage, cancellationToken);
        return output.ToArray();
    }

    private static async Task CopyLimitedAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        string commit,
        string limitMessage,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return;
            total += read;
            if (total > maximumBytes)
                throw new GitSkillPluginRejectedException(commit, limitMessage);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static void ProtectDirectory(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(file, File.GetUnixFileMode(file) & ~(UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
        }
    }

    internal static void DeleteDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(directory, recursive: true);
    }

    private sealed record ArchiveFile(string Path, long Length, TarEntryType EntryType, UnixFileMode Mode)
    {
        public bool IsRegularFile => EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile;
        public bool IsLink => GitSkillPluginAcquirer.IsLink(EntryType);
    }

    private sealed record ArchiveInventory(
        string Format,
        string Name,
        string? Version,
        IReadOnlyList<string> Notices,
        bool SkipInvalidSkills,
        IReadOnlyList<string> SkillRoots,
        HashSet<string> SelectedFiles);

    private sealed class LimitedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maximumBytes;
        private readonly string? _commit;
        private readonly string _message;
        private long _total;

        public LimitedReadStream(Stream inner, long maximumBytes, string? commit, string message)
        {
            _inner = inner;
            _maximumBytes = maximumBytes;
            _commit = commit;
            _message = message;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            CheckLimit(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken);
            CheckLimit(read);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.FromException(new NotSupportedException());
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            await base.DisposeAsync();
        }

        private void CheckLimit(int read)
        {
            _total += read;
            if (_total <= _maximumBytes)
                return;
            if (_commit is null)
                throw new InvalidDataException(_message);
            throw new GitSkillPluginRejectedException(_commit, _message);
        }
    }
}
