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
    /// </summary>
    private static readonly (string Alias, string DisplayName, string RelativePath, bool DefaultAllowSymlinks)[] WellKnownCatalog =
    [
        ("claude-code", "Claude Code", Path.Combine(".claude", "skills"), true),
        ("open-code", "Open Code", Path.Combine(".open-code", "skills"), false)
    ];

    /// <summary>
    /// Ordered list of external skill sources. Precedence follows list order —
    /// earlier sources win on name collisions (native Netclaw skills always take
    /// highest precedence regardless of order).
    /// </summary>
    public List<ExternalSkillSource> Sources { get; set; } = [];

    /// <summary>
    /// Resolves well-known aliases to absolute paths, filters to enabled sources
    /// whose directories exist, and returns the resolved list.
    /// </summary>
    public IReadOnlyList<ResolvedExternalSource> ResolveEnabledSources()
    {
        var results = new List<ResolvedExternalSource>();

        foreach (var source in Sources)
        {
            if (!source.Enabled)
                continue;

            var resolvedPath = source.WellKnown is not null
                ? ResolveWellKnownPath(source.WellKnown)
                : source.Path;

            if (string.IsNullOrWhiteSpace(resolvedPath))
                continue;

            var fullPath = Path.GetFullPath(resolvedPath);
            if (!Directory.Exists(fullPath))
                continue;

            results.Add(new ResolvedExternalSource(source.Name, fullPath, source.AllowSymlinks));
        }

        return results;
    }

    /// <summary>
    /// Probes the filesystem for well-known external skill directories and returns
    /// those that exist on disk, with their default configuration values.
    /// </summary>
    public static IReadOnlyList<WellKnownProbeResult> ProbeWellKnownSources()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var results = new List<WellKnownProbeResult>();

        foreach (var (alias, displayName, relativePath, allowSymlinks) in WellKnownCatalog)
        {
            var path = Path.Combine(home, relativePath);
            if (Directory.Exists(path))
                results.Add(new WellKnownProbeResult(alias, displayName, path, allowSymlinks));
        }

        return results;
    }

    /// <summary>
    /// Maps well-known source aliases to their standard directory paths.
    /// </summary>
    public static string? ResolveWellKnownPath(string wellKnown)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var normalized = wellKnown.ToLowerInvariant();

        foreach (var (alias, _, relativePath, _) in WellKnownCatalog)
        {
            if (alias == normalized)
                return Path.Combine(home, relativePath);
        }

        return null;
    }
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
/// A resolved external skill source with an absolute path ready for scanning.
/// </summary>
public sealed record ResolvedExternalSource(string Name, string Path, bool AllowSymlinks);

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
