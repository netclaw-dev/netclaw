// -----------------------------------------------------------------------
// <copyright file="BuildInfo.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Reflection;

namespace Netclaw.Configuration;

/// <summary>
/// Reads build-time information from assembly metadata.
/// Parameterized by assembly so both CLI and daemon can use it.
/// The commit hash comes from <see cref="AssemblyInformationalVersionAttribute"/>
/// which Microsoft.SourceLink.GitHub populates as "{version}+{full-sha}".
/// The build timestamp comes from an <see cref="AssemblyMetadataAttribute"/> written
/// by Directory.Build.targets at evaluation time.
/// </summary>
public static class BuildInfo
{
    private static Assembly? _assembly;

    /// <summary>
    /// The assembly to read metadata from. Defaults to <see cref="Assembly.GetEntryAssembly"/>.
    /// Set this early in startup if the entry assembly is not the desired source.
    /// </summary>
    public static Assembly TargetAssembly
    {
        get => _assembly ??= Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        set => _assembly = value;
    }

    /// <summary>
    /// Semver version prefix (e.g. "0.1.0").
    /// </summary>
    public static string Version => TargetAssembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Short git commit hash (first 7 chars of the SHA embedded by SourceLink),
    /// or "unknown" if the assembly was built outside a git repository.
    /// </summary>
    public static string CommitHash => ResolveCommitHash(TargetAssembly);

    /// <summary>
    /// UTC build timestamp in ISO-8601 format captured at MSBuild evaluation time,
    /// or "unknown" if not available.
    /// </summary>
    public static string BuildTimestamp =>
        TargetAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildTimestamp")?.Value ?? "unknown";

    /// <summary>
    /// Reads version from a specific assembly without changing the global target.
    /// </summary>
    public static string GetVersion(Assembly assembly) =>
        assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static string ResolveCommitHash(Assembly assembly)
    {
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (informational is null)
            return "unknown";

        var plusIndex = informational.IndexOf('+', StringComparison.Ordinal);
        if (plusIndex < 0 || plusIndex + 1 >= informational.Length)
            return "unknown";

        var fullSha = informational[(plusIndex + 1)..];
        return fullSha.Length >= 7 ? fullSha[..7] : fullSha;
    }
}
