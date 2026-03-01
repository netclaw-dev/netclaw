using System.Reflection;

namespace Netclaw.Daemon;

/// <summary>
/// Exposes build-time information embedded at compile time.
/// The commit hash comes from <see cref="AssemblyInformationalVersionAttribute"/>
/// which Microsoft.SourceLink.GitHub populates as "{version}+{full-sha}".
/// The build timestamp comes from an <see cref="AssemblyMetadataAttribute"/> written
/// by Directory.Build.targets at evaluation time.
/// </summary>
internal static class BuildInfo
{
    private static readonly Assembly Assembly = typeof(BuildInfo).Assembly;

    /// <summary>
    /// Semver version prefix from Directory.Build.props (e.g. "0.1.0").
    /// </summary>
    public static string Version { get; } =
        Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Short git commit hash (first 7 chars of the SHA embedded by SourceLink),
    /// or "unknown" if the assembly was built outside a git repository.
    /// </summary>
    public static string CommitHash { get; } = ResolveCommitHash();

    /// <summary>
    /// UTC build timestamp in ISO-8601 format captured at MSBuild evaluation time,
    /// or "unknown" if not available.
    /// </summary>
    public static string BuildTimestamp { get; } =
        Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "BuildTimestamp")?.Value ?? "unknown";

    private static string ResolveCommitHash()
    {
        var informational = Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (informational is null)
            return "unknown";

        var plusIndex = informational.IndexOf('+');
        if (plusIndex < 0 || plusIndex + 1 >= informational.Length)
            return "unknown";

        var fullSha = informational[(plusIndex + 1)..];
        return fullSha.Length >= 7 ? fullSha[..7] : fullSha;
    }
}
