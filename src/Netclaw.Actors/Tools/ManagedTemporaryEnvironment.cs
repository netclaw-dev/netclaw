// -----------------------------------------------------------------------
// <copyright file="ManagedTemporaryEnvironment.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Netclaw.Security;
using Netclaw.Security.Authorization.Filesystem;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>Prepares a child process to use one session-owned temporary directory.</summary>
internal static class ManagedTemporaryEnvironment
{
    /// <summary>
    /// Validates and creates the directory, checks filesystem links before and after creation,
    /// and sets the standard temporary environment variables on the child process only.
    /// </summary>
    /// <returns><c>null</c> on success; otherwise, a stable error that prevents process launch.</returns>
    internal static string? Prepare(
        ProcessStartInfo startInfo,
        ManagedTemporaryLocation location)
        => Prepare(startInfo, location, HostManagedTemporaryFolderInspector.Instance);

    /// <summary>
    /// Prepares the directory with an explicit folder inspector. For a location with a
    /// <see cref="ManagedTemporaryLocation.PrivateBase"/>, the base and the key folder must
    /// each be a folder that is not a link, that the daemon user owns, and that has no
    /// group or other permission bits. A failed check refuses the launch (issue #2554).
    /// </summary>
    /// <remarks>Issue #2554 test-author stub: the inspector is not used yet.</remarks>
    internal static string? Prepare(
        ProcessStartInfo startInfo,
        ManagedTemporaryLocation location,
        IManagedTemporaryFolderInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(inspector);

        try
        {
            var normalizedTemporaryDirectory = ShellExecutionEnvironment.GetTemporaryDirectoryValue(location);
            if (!CanonicalPath.TryCreateHost(location.StorageRoot.Value, relativeBase: null, out var root)
                || !CanonicalPath.TryCreateHost(normalizedTemporaryDirectory, relativeBase: null, out var temporary))
            {
                return "Error: The managed temporary directory contains an unsafe filesystem link.";
            }

            // Check before and after creation: a link can appear between the two.
            PathBoundary[] storage = [new PathBoundary.Folder(root, LinkRule.IncludingRoot)];
            if (FileSystemAuthority.EvaluateMembership(temporary, storage) is not PathDecision.Allowed)
                return "Error: The managed temporary directory contains an unsafe filesystem link.";

            Directory.CreateDirectory(normalizedTemporaryDirectory);
            if (!Directory.Exists(normalizedTemporaryDirectory))
                return $"Error: Managed temporary directory '{normalizedTemporaryDirectory}' was not created.";

            if (FileSystemAuthority.EvaluateMembership(temporary, storage) is not PathDecision.Allowed)
                return "Error: The managed temporary directory contains an unsafe filesystem link.";

            // The shell parser resolves these variables from the same values (ShellExecutionEnvironment.CreateLaunchEnvironment).
            ShellExecutionEnvironment.ApplyTemporaryVariables(startInfo.Environment, location);
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or IOException
                                   or NotSupportedException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            return $"Error preparing managed temporary directory: {ex.Message}";
        }
    }
}

/// <summary>
/// Reads the facts of one folder without following a link at the final path segment
/// (<c>lstat</c>). The private-folder checks of <see cref="ManagedTemporaryEnvironment"/> use it.
/// </summary>
internal interface IManagedTemporaryFolderInspector
{
    /// <summary>Gets the effective user ID of the daemon process.</summary>
    uint EffectiveUserId { get; }

    /// <summary>Reads the facts of a path. Returns <c>false</c> when the path does not exist.</summary>
    bool TryInspect(string path, out ManagedTemporaryFolderFacts facts);
}

/// <summary>The facts of one folder that the private-folder checks need.</summary>
/// <param name="IsSymbolicLink">True when the path itself is a symbolic link.</param>
/// <param name="OwnerUserId">The user ID that owns the path.</param>
/// <param name="Mode">The permission bits of the path.</param>
internal readonly record struct ManagedTemporaryFolderFacts(
    bool IsSymbolicLink,
    uint OwnerUserId,
    UnixFileMode Mode);

/// <summary>Reads folder facts from the host filesystem.</summary>
internal sealed class HostManagedTemporaryFolderInspector : IManagedTemporaryFolderInspector
{
    internal static HostManagedTemporaryFolderInspector Instance { get; } = new();

    private HostManagedTemporaryFolderInspector()
    {
    }

    // Issue #2554 test-author stub. The implementer reads geteuid() and lstat().
    public uint EffectiveUserId
        => throw new NotImplementedException("Issue #2554: HostManagedTemporaryFolderInspector is not implemented yet.");

    public bool TryInspect(string path, out ManagedTemporaryFolderFacts facts)
        => throw new NotImplementedException("Issue #2554: HostManagedTemporaryFolderInspector is not implemented yet.");
}
