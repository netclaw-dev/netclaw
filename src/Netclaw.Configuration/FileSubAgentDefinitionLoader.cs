// -----------------------------------------------------------------------
// <copyright file="FileSubAgentDefinitionLoader.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;

namespace Netclaw.Configuration;

/// <summary>
/// Loads subagent definitions from <c>.md</c> files in the agents directory.
/// Each agent is a single markdown file: YAML frontmatter carries metadata and
/// the body is the system prompt verbatim. Matches the de facto Claude Code /
/// OpenCode format and the <c>SKILL.md</c> pattern Netclaw already uses for skills.
/// Called during startup after MCP discovery so tool names are resolvable.
/// </summary>
public sealed class FileSubAgentDefinitionLoader
{
    private sealed record LoadSnapshot(string Fingerprint, IReadOnlyList<SubAgentProfile> Profiles);
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
        var profiles = LoadProfilesFromDisk();
        var newSnapshot = new LoadSnapshot(fingerprint, profiles);

        lock (_snapshotGate)
        {
            if (_lastSnapshot is null || !string.Equals(_lastSnapshot.Fingerprint, fingerprint, StringComparison.Ordinal))
                _lastSnapshot = newSnapshot;
            return _lastSnapshot;
        }
    }

    private IReadOnlyList<SubAgentProfile> LoadProfilesFromDisk()
    {
        if (!Directory.Exists(_agentsDirectory))
        {
            _logger.LogWarning("Agents directory does not exist: {Path}", _agentsDirectory);
            return [];
        }

        var files = EnumerateAgentDefinitionFiles();
        if (files.Count == 0)
        {
            _logger.LogWarning("No agent definition files found in {Path}", _agentsDirectory);
            return [];
        }

        var results = new List<SubAgentProfile>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var profile = TryParse(file.Path);
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

        return results;
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

    private SubAgentProfile? TryParse(string filePath)
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

        return TryParseDefinition(filePath, content, _logger);
    }

    public static SubAgentProfile? TryParseDefinition(string sourcePath, string content, ILogger logger)
    {
        var frontmatter = SubAgentMarkdownParser.ExtractFrontmatter(content);
        if (frontmatter is null)
        {
            logger.LogWarning(
                "Agent definition at {Path} has missing or unparseable YAML frontmatter — skipping",
                sourcePath);
            return null;
        }

        if (string.IsNullOrWhiteSpace(frontmatter.Name))
        {
            logger.LogWarning("Agent definition at {Path} has no 'name' in frontmatter — skipping", sourcePath);
            return null;
        }

        if (string.IsNullOrWhiteSpace(frontmatter.Description))
        {
            logger.LogWarning(
                "Agent '{Name}' at {Path} has no 'description' in frontmatter — skipping",
                frontmatter.Name, sourcePath);
            return null;
        }

        var systemPrompt = SubAgentMarkdownParser.ExtractBody(content);
        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            logger.LogWarning(
                "Agent '{Name}' at {Path} has an empty system prompt body — skipping",
                frontmatter.Name, sourcePath);
            return null;
        }

        // Tool metadata is optional and advisory. Runtime access is resolved from
        // the parent session audience at spawn time.
        var tools = frontmatter.Tools ?? [];

        if (!TryParseModelRole(frontmatter.ModelRole, out var modelRole))
        {
            logger.LogWarning(
                "Agent '{Name}' at {Path} has invalid modelRole '{Value}' (expected Main or Compaction) — skipping",
                frontmatter.Name, sourcePath, frontmatter.ModelRole);
            return null;
        }

        if (!TryParseVisibility(frontmatter.Visibility, out var visibility))
        {
            logger.LogWarning(
                "Agent '{Name}' at {Path} has invalid visibility '{Value}' (expected user-facing or internal) — skipping",
                frontmatter.Name, sourcePath, frontmatter.Visibility);
            return null;
        }

        // Validate timeout overrides against the same bounds the JSON config schema
        // enforces. Fail loud rather than letting an out-of-range value flow to
        // ITimerScheduler (a non-positive timeout throws) or silently exceed the
        // documented ceiling — the frontmatter path bypasses schema validation.
        if (frontmatter.TimeoutSeconds is { } timeoutSeconds && timeoutSeconds is < 5 or > 600)
        {
            logger.LogWarning(
                "Agent '{Name}' at {Path} has invalid timeoutSeconds {Value} (expected 5–600) — skipping",
                frontmatter.Name, sourcePath, timeoutSeconds);
            return null;
        }

        if (frontmatter.PrefillTimeoutSeconds is { } prefillSeconds && prefillSeconds is < 5 or > 3600)
        {
            logger.LogWarning(
                "Agent '{Name}' at {Path} has invalid prefillTimeoutSeconds {Value} (expected 5–3600) — skipping",
                frontmatter.Name, sourcePath, prefillSeconds);
            return null;
        }

        return new SubAgentProfile
        {
            Name = frontmatter.Name.Trim(),
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
