using System.Reflection;

namespace Netclaw.Daemon;

/// <summary>
/// Daemon-specific <see cref="Configuration.BuildInfo"/> facade.
/// Reads from the daemon assembly and delegates to the shared implementation.
/// </summary>
internal static class BuildInfo
{
    private static readonly Assembly Assembly = typeof(BuildInfo).Assembly;

    /// <summary>
    /// Semver version prefix from Directory.Build.props (e.g. "0.1.0").
    /// </summary>
    public static string Version { get; } =
        Netclaw.Configuration.BuildInfo.GetVersion(Assembly);

    /// <summary>
    /// Short git commit hash (first 7 chars of the SHA embedded by SourceLink),
    /// or "unknown" if the assembly was built outside a git repository.
    /// </summary>
    public static string CommitHash { get; } =
        Netclaw.Configuration.BuildInfo.ResolveCommitHash(Assembly);

    /// <summary>
    /// UTC build timestamp in ISO-8601 format captured at MSBuild evaluation time,
    /// or "unknown" if not available.
    /// </summary>
    public static string BuildTimestamp { get; } =
        Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "BuildTimestamp")?.Value ?? "unknown";
}
