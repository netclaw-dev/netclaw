// -----------------------------------------------------------------------
// <copyright file="FileSubAgentDefinitionLoader.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Netclaw.Configuration;

/// <summary>
/// A subagent definition file that failed validation. <see cref="Name"/> is null when the
/// frontmatter has no usable name. <see cref="Reason"/> completes the sentence
/// "Agent '{name}' at {path} ..." and does not repeat the path.
/// </summary>
public sealed record SubAgentFileRejection(string Path, string? Name, string Reason);

/// <summary>
/// Loads subagent definitions from <c>.md</c> files in the agents directory.
/// Each agent is a single markdown file: YAML frontmatter carries metadata and
/// the body is the system prompt verbatim. Matches the de facto Claude Code /
/// OpenCode format and the <c>SKILL.md</c> pattern Netclaw already uses for skills.
/// Called during startup after MCP discovery so tool names are resolvable.
/// </summary>
public sealed class FileSubAgentDefinitionLoader
{
    private sealed record LoadSnapshot(
        string Fingerprint,
        IReadOnlyList<SubAgentProfile> Profiles,
        IReadOnlyList<SubAgentFileRejection> Rejections);
    private sealed record AgentDefinitionFile(string Path, string? FeedName);

    private readonly string _agentsDirectory;
    private readonly string _serverFeedAgentsDirectory;
    private readonly SkillFeedsConfig? _feedsConfig;
    private readonly ILogger<FileSubAgentDefinitionLoader> _logger;
    private readonly object _snapshotGate = new();
    private LoadSnapshot? _lastSnapshot;

    public FileSubAgentDefinitionLoader(
        NetclawPaths paths,
        ILogger<FileSubAgentDefinitionLoader> logger,
        SkillFeedsConfig? feedsConfig = null)
    {
        _agentsDirectory = paths.AgentsDirectory;
        _serverFeedAgentsDirectory = paths.ServerFeedAgentsDirectory;
        _feedsConfig = feedsConfig;
        _logger = logger;
    }

    /// <summary>
    /// Scan the agents directory for <c>*.md</c> files and return parsed profiles.
    /// Malformed files are rejected with a loud warning and skipped — no silent fallback.
    /// Duplicate <c>name</c> values across files are rejected for all but the first occurrence.
    /// </summary>
    public IReadOnlyList<SubAgentProfile> LoadAll()
    {
        return LoadCurrentSnapshot(ComputeDirectoryFingerprint()).Profiles;
    }

    /// <summary>
    /// Returns the rejected file for an agent name in the latest load, when one exists.
    /// Call <see cref="SyncInto"/> first so the snapshot is current.
    /// </summary>
    public bool TryGetRejection(string name, [NotNullWhen(true)] out SubAgentFileRejection? rejection)
    {
        lock (_snapshotGate)
        {
            rejection = _lastSnapshot?.Rejections.FirstOrDefault(
                r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            return rejection is not null;
        }
    }

    public bool RefreshIfChanged(out IReadOnlyList<SubAgentProfile> profiles)
    {
        var fingerprint = ComputeDirectoryFingerprint();

        lock (_snapshotGate)
        {
            if (_lastSnapshot is not null && string.Equals(_lastSnapshot.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                profiles = _lastSnapshot.Profiles;
                return false;
            }
        }

        profiles = LoadCurrentSnapshot(fingerprint).Profiles;
        return true;
    }

    /// <summary>
    /// Detect on-disk changes and, if any, replace the registry's file-loaded
    /// profiles. Returns true when the registry was modified.
    /// </summary>
    public bool SyncInto(SubAgentDefinitionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        if (!RefreshIfChanged(out var profiles))
            return false;

        registry.ReplaceFileProfiles(profiles);
        return true;
    }

    private LoadSnapshot LoadCurrentSnapshot(string fingerprint)
    {
        lock (_snapshotGate)
        {
            if (_lastSnapshot is not null && string.Equals(_lastSnapshot.Fingerprint, fingerprint, StringComparison.Ordinal))
                return _lastSnapshot;
        }

        // Read disk outside the lock so other callers aren't blocked. A concurrent loader
        // may race us; the gate-protected install below accepts whichever snapshot lands
        // first with the same fingerprint.
        var (profiles, rejections) = LoadProfilesFromDisk();
        var newSnapshot = new LoadSnapshot(fingerprint, profiles, rejections);

        lock (_snapshotGate)
        {
            if (_lastSnapshot is null || !string.Equals(_lastSnapshot.Fingerprint, fingerprint, StringComparison.Ordinal))
                _lastSnapshot = newSnapshot;
            return _lastSnapshot;
        }
    }

    private (IReadOnlyList<SubAgentProfile> Profiles, IReadOnlyList<SubAgentFileRejection> Rejections) LoadProfilesFromDisk()
    {
        if (!Directory.Exists(_agentsDirectory))
        {
            _logger.LogWarning("Agents directory does not exist: {Path}", _agentsDirectory);
            return ([], []);
        }

        var files = EnumerateAgentDefinitionFiles();
        if (files.Count == 0)
        {
            _logger.LogWarning("No agent definition files found in {Path}", _agentsDirectory);
            return ([], []);
        }

        var results = new List<SubAgentProfile>();
        var rejections = new List<SubAgentFileRejection>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var profile = TryParse(file.Path, rejections);
            if (profile is null)
                continue;

            if (!seenNames.Add(profile.Name))
            {
                LogDuplicate(file, profile.Name);
                continue;
            }

            results.Add(profile);
            _logger.LogInformation(
                "Loaded agent definition: {Name} (tool metadata={ToolCount}, timeout={Timeout}s)",
                profile.Name, profile.ToolNames.Count, profile.TimeoutSeconds);
        }

        return (results, rejections);
    }

    private string ComputeDirectoryFingerprint()
    {
        if (!Directory.Exists(_agentsDirectory))
            return "missing";

        // Length is part of the fingerprint so rapid edits within a single mtime tick
        // still register as a change. mtime alone is unreliable on low-resolution filesystems
        // and during fast successive writes.
        var files = EnumerateAgentDefinitionFiles()
            .Select(file =>
            {
                var path = file.Path;
                var info = new FileInfo(path);
                return $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            });

        return string.Join(";", files);
    }

    private IReadOnlyList<AgentDefinitionFile> EnumerateAgentDefinitionFiles()
    {
        var files = new List<AgentDefinitionFile>();
        if (!Directory.Exists(_agentsDirectory))
            return files;

        files.AddRange(Directory.GetFiles(_agentsDirectory, "*.md", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(path => new AgentDefinitionFile(path, FeedName: null)));

        if (!Directory.Exists(_serverFeedAgentsDirectory))
            return files;

        foreach (var feedName in EnumerateManagedFeedNames())
        {
            var feedDir = Path.Combine(_serverFeedAgentsDirectory, feedName);
            if (!Directory.Exists(feedDir))
                continue;

            files.AddRange(Directory.GetFiles(feedDir, "*.md", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(path => new AgentDefinitionFile(path, feedName)));
        }

        return files;
    }

    private IEnumerable<string> EnumerateManagedFeedNames()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (_feedsConfig is not null)
        {
            foreach (var feed in _feedsConfig.Feeds.Where(f => f.Enabled))
            {
                if (!IsSafeManagedFeedName(feed.Name))
                {
                    _logger.LogWarning(
                        "Ignoring managed sub-agent feed '{FeedName}' because it is not a safe feed name",
                        feed.Name);
                    continue;
                }

                if (seen.Add(feed.Name))
                    yield return feed.Name;
            }

            yield break;
        }

        foreach (var dir in Directory.GetDirectories(_serverFeedAgentsDirectory)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var feedName = Path.GetFileName(dir);
            if (!string.IsNullOrEmpty(feedName) && seen.Add(feedName))
                yield return feedName;
        }
    }

    private static bool IsSafeManagedFeedName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        foreach (var c in value)
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')
                continue;

            return false;
        }

        return true;
    }

    private void LogDuplicate(AgentDefinitionFile file, string name)
    {
        if (file.FeedName is null)
        {
            _logger.LogWarning(
                "Agent definition at {Path} declares duplicate name '{Name}' — skipping",
                file.Path,
                name);
            return;
        }

        _logger.LogWarning(
            "Managed agent definition at {Path} from feed '{FeedName}' declares duplicate name '{Name}' — skipping; earlier definition wins",
            file.Path,
            file.FeedName,
            name);
    }

    private SubAgentProfile? TryParse(string filePath, List<SubAgentFileRejection> rejections)
    {
        string content;
        try
        {
            content = File.ReadAllText(filePath);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to read agent definition: {Path}", filePath);
            return null;
        }

        var profile = TryParseDefinition(filePath, content, _logger, out var rejection);
        if (rejection is not null)
            rejections.Add(rejection);

        return profile;
    }

    public static SubAgentProfile? TryParseDefinition(string sourcePath, string content, ILogger logger)
        => TryParseDefinition(sourcePath, content, logger, out _);

    /// <summary>
    /// Parses one definition file. A rejected file logs one warning, returns null, and sets
    /// <paramref name="rejection"/> so the caller can name the file and reason to the model.
    /// </summary>
    internal static SubAgentProfile? TryParseDefinition(
        string sourcePath,
        string content,
        ILogger logger,
        out SubAgentFileRejection? rejection)
    {
        rejection = null;

        var frontmatter = SubAgentMarkdownParser.ExtractFrontmatter(content);
        if (frontmatter is null)
            return Reject(sourcePath, null, "has missing or unparseable YAML frontmatter", logger, out rejection);

        if (string.IsNullOrWhiteSpace(frontmatter.Name))
            return Reject(sourcePath, null, "has no 'name' in frontmatter", logger, out rejection);

        var name = frontmatter.Name.Trim();
        if (string.IsNullOrWhiteSpace(frontmatter.Description))
            return Reject(sourcePath, name, "has no 'description' in frontmatter", logger, out rejection);

        var systemPrompt = SubAgentMarkdownParser.ExtractBody(content);
        if (string.IsNullOrWhiteSpace(systemPrompt))
            return Reject(sourcePath, name, "has an empty system prompt body", logger, out rejection);

        // Tool metadata is optional and advisory. Runtime access is resolved from
        // the parent session audience at spawn time.
        var tools = frontmatter.Tools ?? [];

        if (!TryParseModelRole(frontmatter.ModelRole, out var modelRole))
            return Reject(sourcePath, name,
                $"has invalid modelRole '{frontmatter.ModelRole}' (expected Main or Compaction)", logger, out rejection);

        if (!TryParseVisibility(frontmatter.Visibility, out var visibility))
            return Reject(sourcePath, name,
                $"has invalid visibility '{frontmatter.Visibility}' (expected user-facing or internal)", logger, out rejection);

        // Validate timeout overrides against the same bounds the JSON config schema
        // enforces. Fail loud rather than letting an out-of-range value flow to
        // ITimerScheduler (a non-positive timeout throws) or silently exceed the
        // documented ceiling — the frontmatter path bypasses schema validation.
        if (frontmatter.TimeoutSeconds is { } timeoutSeconds && timeoutSeconds is < 5 or > 600)
            return Reject(sourcePath, name,
                $"has invalid timeoutSeconds {timeoutSeconds} (expected 5–600)", logger, out rejection);

        if (frontmatter.PrefillTimeoutSeconds is { } prefillSeconds && prefillSeconds is < 5 or > 3600)
            return Reject(sourcePath, name,
                $"has invalid prefillTimeoutSeconds {prefillSeconds} (expected 5–3600)", logger, out rejection);

        return new SubAgentProfile
        {
            Name = name,
            Description = frontmatter.Description.Trim(),
            SystemPrompt = systemPrompt,
            ToolNames = tools,
            ModelRole = modelRole,
            TimeoutSeconds = frontmatter.TimeoutSeconds ?? 60,
            PrefillTimeoutSeconds = frontmatter.PrefillTimeoutSeconds,
            EmitStructuredFindings = frontmatter.EmitStructuredFindings ?? false,
            Visibility = visibility
        };
    }

    private static SubAgentProfile? Reject(
        string sourcePath,
        string? name,
        string reason,
        ILogger logger,
        out SubAgentFileRejection rejection)
    {
        rejection = new SubAgentFileRejection(sourcePath, name, reason);
        if (name is null)
            logger.LogWarning("Agent definition at {Path} {Reason} — skipping", sourcePath, reason);
        else
            logger.LogWarning("Agent '{Name}' at {Path} {Reason} — skipping", name, sourcePath, reason);
        return null;
    }

    private static bool TryParseModelRole(string? value, out ModelRole role)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            role = ModelRole.Compaction;
            return true;
        }

        return Enum.TryParse(value, ignoreCase: true, out role);
    }

    private static bool TryParseVisibility(string? value, out SubAgentVisibility visibility)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            visibility = SubAgentVisibility.UserFacing;
            return true;
        }

        // Accept both `user-facing` (frontmatter convention) and `UserFacing` (enum name).
        var normalized = value.Replace("-", "", StringComparison.Ordinal);
        return Enum.TryParse(normalized, ignoreCase: true, out visibility);
    }
}
