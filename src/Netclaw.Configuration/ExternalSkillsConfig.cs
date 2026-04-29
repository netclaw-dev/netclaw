// -----------------------------------------------------------------------
// <copyright file="ExternalSkillsConfig.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Configuration for loading skills from external directories
/// (e.g., Claude Code, Open Code, or custom team skill directories).
/// </summary>
public sealed class ExternalSkillsConfig
{
    /// <summary>
    /// Single catalog of well-known external skill sources. Both
    /// <see cref="ResolveWellKnownPath"/> and <see cref="ProbeWellKnownSources"/>
    /// consume this so alias/display/symlink metadata stays in one place.
    /// Each alias can own multiple relative paths — the first path is the
    /// primary (used for display/validation); all existing paths are scanned.
    /// </summary>
    /// <remarks>
    /// The <c>claude-code</c> alias includes both <c>~/.claude/skills/</c> and
    /// <c>~/.claude/commands/</c>. Claude Code treats command markdown files as
    /// skills, so Netclaw must scan both locations. The alias is also expanded
    /// at resolution time by <see cref="ResolveEnabledSources(string)"/> to
    /// include every installed plugin marketplace under
    /// <c>~/.claude/plugins/marketplaces/*/skills/</c>, so marketplace skills
    /// (e.g. the dotnet-skills plugin) show up without needing a separate
    /// configured source. That marketplace expansion is dynamic and lives
    /// outside the static catalog.
    /// </remarks>
    private static readonly (string Alias, string DisplayName, string[] RelativePaths, bool DefaultAllowSymlinks)[] WellKnownCatalog =
    [
        (ClaudeCodeAlias, "Claude Code",
            new[] { Path.Combine(".claude", "skills"), Path.Combine(".claude", "commands") },
            true),
        ("open-code", "Open Code",
            new[] { Path.Combine(".open-code", "skills") },
            false)
    ];

    /// <summary>
    /// Well-known alias whose resolution also enumerates Claude Code plugin
    /// marketplaces. Kept as a constant so the dynamic-expansion branch in
    /// <see cref="ResolveEnabledSources(string)"/> stays discoverable.
    /// </summary>
    internal const string ClaudeCodeAlias = "claude-code";

    /// <summary>
    /// Relative path from the home directory to the Claude Code plugins root
    /// whose subdirectories each contain a <c>skills/</c> folder for an
    /// installed marketplace.
    /// </summary>
    private static readonly string ClaudeCodeMarketplacesRelativePath =
        Path.Combine(".claude", "plugins", "marketplaces");

    /// <summary>
    /// Ordered list of external skill sources. Precedence follows list order —
    /// earlier sources win on name collisions (native Netclaw skills always take
    /// highest precedence regardless of order).
    /// </summary>
    public List<ExternalSkillSource> Sources { get; set; } = [];

    /// <summary>
    /// Resolves well-known aliases to absolute paths, filters to enabled sources
    /// whose directories exist, and returns the resolved list. Each returned
    /// <see cref="ResolvedExternalSource"/> carries all existing scan paths
    /// for its alias — a single configured source may scan multiple directories.
    /// </summary>
    public IReadOnlyList<ResolvedExternalSource> ResolveEnabledSources()
        => ResolveEnabledSources(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// Testable overload that resolves sources against a given home directory
    /// rather than the current user's profile.
    /// </summary>
    internal IReadOnlyList<ResolvedExternalSource> ResolveEnabledSources(string homeDirectory)
    {
        var results = new List<ResolvedExternalSource>();

        foreach (var source in Sources)
        {
            if (!source.Enabled)
                continue;

            IReadOnlyList<string> candidatePaths = source.WellKnown is not null
                ? ResolveWellKnownPaths(source.WellKnown, homeDirectory)
                : source.Path is not null ? new[] { source.Path } : [];

            if (string.Equals(source.WellKnown, ClaudeCodeAlias, StringComparison.OrdinalIgnoreCase))
            {
                var marketplacePaths = EnumerateClaudeCodeMarketplaceSkillPaths(homeDirectory);
                if (marketplacePaths.Count > 0)
                    candidatePaths = candidatePaths.Concat(marketplacePaths).ToList();
            }

            var existingPaths = new List<string>();
            var seenPaths = new HashSet<string>(GetPathComparer());
            foreach (var candidate in candidatePaths)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;

                var fullPath = Path.GetFullPath(candidate);
                if (!Directory.Exists(fullPath))
                    continue;

                if (seenPaths.Add(fullPath))
                    existingPaths.Add(fullPath);
            }

            if (existingPaths.Count == 0)
                continue;

            results.Add(new ResolvedExternalSource(source.Name, existingPaths, source.AllowSymlinks));
        }

        return results;
    }

    /// <summary>
    /// Enumerates the live <c>skills/</c> directories of every Claude Code
    /// plugin marketplace installed under <c>~/.claude/plugins/marketplaces/</c>.
    /// The filesystem is the source of truth — we intentionally don't parse
    /// <c>known_marketplaces.json</c> or <c>installed_plugins.json</c> so
    /// Netclaw stays decoupled from Claude Code's plugin metadata format. The
    /// version cache at <c>plugins/cache/</c> is skipped because Claude Code
    /// itself reads the live marketplace path at runtime; scanning the cache
    /// would duplicate entries.
    /// </summary>
    private static IReadOnlyList<string> EnumerateClaudeCodeMarketplaceSkillPaths(string homeDirectory)
    {
        var marketplacesRoot = Path.Combine(homeDirectory, ClaudeCodeMarketplacesRelativePath);
        if (!Directory.Exists(marketplacesRoot))
            return Array.Empty<string>();

        var pathComparer = GetPathComparer();

        return Directory.EnumerateDirectories(marketplacesRoot)
            .Select(d => Path.GetFullPath(Path.Combine(d, "skills")))
            .Where(Directory.Exists)
            .Distinct(pathComparer)
            .OrderBy(p => p, pathComparer)
            .ToList();
    }

    /// <summary>
    /// Probes the filesystem for well-known external skill directories and returns
    /// those where at least one configured path exists on disk. <see cref="WellKnownProbeResult.ResolvedPath"/>
    /// is the first existing path (primary preferred).
    /// </summary>
    public static IReadOnlyList<WellKnownProbeResult> ProbeWellKnownSources()
        => ProbeWellKnownSources(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// Testable overload that probes against a given home directory.
    /// </summary>
    internal static IReadOnlyList<WellKnownProbeResult> ProbeWellKnownSources(string homeDirectory)
    {
        var results = new List<WellKnownProbeResult>();

        foreach (var (alias, displayName, relativePaths, allowSymlinks) in WellKnownCatalog)
        {
            string? firstExisting = null;
            foreach (var relativePath in relativePaths)
            {
                var path = Path.Combine(homeDirectory, relativePath);
                if (Directory.Exists(path))
                {
                    firstExisting = path;
                    break;
                }
            }

            if (firstExisting is null
                && string.Equals(alias, ClaudeCodeAlias, StringComparison.Ordinal)
                && EnumerateClaudeCodeMarketplaceSkillPaths(homeDirectory) is { Count: > 0 } marketplacePaths)
            {
                firstExisting = marketplacePaths[0];
            }

            if (firstExisting is not null)
                results.Add(new WellKnownProbeResult(alias, displayName, firstExisting, allowSymlinks));
        }

        return results;
    }

    /// <summary>
    /// Maps a well-known source alias to its primary directory path. Returns
    /// <c>null</c> for unknown aliases. Used for validation and display — for
    /// the full scan-path set, use <see cref="ResolveWellKnownPaths(string)"/>.
    /// </summary>
    public static string? ResolveWellKnownPath(string wellKnown)
    {
        var paths = ResolveWellKnownPaths(wellKnown);
        return paths.Count > 0 ? paths[0] : null;
    }

    /// <summary>
    /// Maps a well-known source alias to all of its standard directory paths.
    /// Returns an empty list for unknown aliases.
    /// </summary>
    public static IReadOnlyList<string> ResolveWellKnownPaths(string wellKnown)
        => ResolveWellKnownPaths(wellKnown, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static IReadOnlyList<string> ResolveWellKnownPaths(string wellKnown, string homeDirectory)
    {
        var normalized = wellKnown.ToLowerInvariant();

        foreach (var (alias, _, relativePaths, _) in WellKnownCatalog)
        {
            if (alias == normalized)
                return relativePaths.Select(p => Path.Combine(homeDirectory, p)).ToArray();
        }

        return Array.Empty<string>();
    }

    private static StringComparer GetPathComparer()
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

/// <summary>
/// Configuration for a single external skill source.
/// </summary>
public sealed class ExternalSkillSource
{
    /// <summary>
    /// Unique identifier for this source (e.g., "claude-code", "team-skills").
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Absolute path to the skill directory. Mutually exclusive with <see cref="WellKnown"/>.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Well-known source alias that resolves to a standard path.
    /// Supported values: "claude-code", "open-code".
    /// Mutually exclusive with <see cref="Path"/>.
    /// </summary>
    public string? WellKnown { get; set; }

    /// <summary>
    /// Whether this source is active. Default: <c>true</c>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether to allow symlinks within this external directory.
    /// Resolved paths are still validated to stay within the source root.
    /// Default: <c>false</c>.
    /// </summary>
    public bool AllowSymlinks { get; set; }
}

/// <summary>
/// A resolved external skill source with one or more absolute paths ready for
/// scanning. A single configured source (e.g. <c>claude-code</c>) may expand
/// to multiple paths when its well-known alias covers more than one directory.
/// </summary>
public sealed record ResolvedExternalSource(string Name, IReadOnlyList<string> Paths, bool AllowSymlinks);

/// <summary>
/// Result of probing for a well-known external skill directory.
/// </summary>
/// <param name="WellKnownAlias">The alias used in config (e.g., "claude-code").</param>
/// <param name="DisplayName">Human-readable name (e.g., "Claude Code").</param>
/// <param name="ResolvedPath">Absolute path on disk.</param>
/// <param name="DefaultAllowSymlinks">Whether this source should allow symlinks by default.</param>
public sealed record WellKnownProbeResult(
    string WellKnownAlias,
    string DisplayName,
    string ResolvedPath,
    bool DefaultAllowSymlinks);
